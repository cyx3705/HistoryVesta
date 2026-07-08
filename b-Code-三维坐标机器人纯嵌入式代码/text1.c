#include "STC8A8K64D4.H"

/*
 * Buttons (active-low):
 *   P0.0 -> direction key (released=1, pressed=0)
 *   P0.1 -> X jog
 *   P0.2 -> Y jog
 *   P0.3 -> Z jog
 *   P0.5 -> scan start
 *   P0.4 -> stop / replay trigger
 *
 * Outputs:
 *   P2.0 -> X CLK
 *   P2.1 -> X DIR
 *   P2.2 -> Y CLK
 *   P2.3 -> Y DIR
 *   P2.4 -> Z CLK
 *   P2.5 -> Z DIR
 */

#define CLK_MASK 0x15  /* P2.0 / P2.2 / P2.4 */
#define X_CLK_BIT 0x01
#define Y_CLK_BIT 0x04
#define Z_CLK_BIT 0x10

#define DIR_X_BIT 0x02
#define DIR_Y_BIT 0x08
#define DIR_Z_BIT 0x20

/* ------------------------------------------------------------------ *
 * Per-axis CRUISE(working) pulse frequency  (指导书 传动计算, 20mm/s)
 *
 *   X 轴  同步带  导程95mm  4细分  减速比1:10
 *         95/200/4/10 = 0.0119 mm  ->  20/0.0119 = 1680 Hz
 *   Y 轴  滚珠丝杠 导程5mm  4细分  减速比1:1(联轴器直连)
 *         5/200/4/1  = 0.00625 mm ->  20/0.00625 = 3200 Hz
 *   Z 轴  滚珠丝杠 导程5mm  4细分  减速比1:1(联轴器直连)
 *         5/200/4/1  = 0.00625 mm ->  20/0.00625 = 3200 Hz
 *
 * DDS(相位累加器)时基: 基准节拍 F_BASE, 每 tick 给每轴累加 2*f, 累到
 * >=F_BASE 翻转一次 CLK 电平. 频率"比例"由整数常数精确锁定, "绝对值"由
 * 基准节拍决定 -> 只需标定 BASE_DELAY_COUNT 一个旋钮(示波器测 Y/Z=3200Hz).
 * ------------------------------------------------------------------ */
#define F_BASE_HZ 12800U
#define FREQ_X_HZ 1680U
#define FREQ_Y_HZ 3200U
#define FREQ_Z_HZ 3200U
#define CRUISE_X (2U * FREQ_X_HZ)   /* 3360 */
#define CRUISE_Y (2U * FREQ_Y_HZ)   /* 6400 */
#define CRUISE_Z (2U * FREQ_Z_HZ)   /* 6400 */

/* ------------------------------------------------------------------ *
 * 梯形加减速(直线加减速, 频率域线性斜坡):
 *   - 起步不从 0 开始, 而是从"起动频率"START_FREQ_HZ 起跳(<=极限起动频率),
 *   - 每个基准 tick 让当前速度按 ACCEL_STEP 线性靠近目标(加速/减速),
 *   - 减速到起动频率后停止(指导书: 减速到起动速度再停).
 * 因果律: 按下按钮=目标改为巡航(左斜坡加速, 梯形左下角起);
 *         抬起按钮=目标改为停止(右斜坡减速, 梯形右上角起).
 *   ACCEL_STEP 越大加速越快. Y/Z: (6400-1000)/2 = 2700 tick ~ 0.21s 到速.
 * ------------------------------------------------------------------ */
#define START_FREQ_HZ 500U
#define START_INCR (2U * START_FREQ_HZ)   /* 1000 */
#define ACCEL_STEP 2U                     /* 每 tick 速度(增量)变化步长 */
#define DECEL_RESERVE 300                 /* 回放:剩余脉冲少于此值时开始减速 */

/* base timing / behaviour */
#define BASE_DELAY_COUNT 45   /* 标定: 调到 Y/Z CLK = 3200Hz, X 自动=1680Hz */
#define SCAN_Z_PREMOVE_PULSES 120
#define SCAN_X_FORWARD 0
#define KEY_DEBOUNCE_MS 20

typedef signed long i32;

static void IO_Init(void);
static bit key_pressed(unsigned char mask);
static void set_axis_dir_x(bit forward);
static void set_axis_dir_y(bit forward);
static void set_axis_dir_z(bit forward);
static void clear_clk(void);
static void base_delay(void);
static void delay_ms(unsigned int ms);
static bit any_replay_remaining(void);
static unsigned int ramp(unsigned int cur, unsigned int target);
static unsigned char dds_tick(void);
static void pre_move_z_on_scan_start(void);

/* scan records: pulse counts by axis+direction */
static i32 rec_x_pos = 0;
static i32 rec_x_neg = 0;
static i32 rec_y_pos = 0;
static i32 rec_y_neg = 0;
static i32 rec_z_pos = 0;
static i32 rec_z_neg = 0;

/* replay remaining counts */
static i32 play_x_pos = 0;
static i32 play_x_neg = 0;
static i32 play_y_pos = 0;
static i32 play_y_neg = 0;
static i32 play_z_pos = 0;
static i32 play_z_neg = 0;

/* motion_state: 0=stop/idle, 1=scan running, 2=replay running */
static unsigned char motion_state = 0;
/* while scanning: 1 = a stop was requested, X is ramping down (still recording) */
static bit scan_stop_req = 0;

/* DDS engine state: phase accumulators, current CLK levels, current speeds */
static unsigned int acc_x = 0;
static unsigned int acc_y = 0;
static unsigned int acc_z = 0;
static unsigned char clk_level = 0;   /* current high/low of each CLK bit */
static unsigned int inc_x = 0;        /* current DDS increment == current speed */
static unsigned int inc_y = 0;
static unsigned int inc_z = 0;

static void IO_Init(void)
{
    P_SW2 |= 0x80;

    /* P2.0~P2.5 push-pull outputs */
    P2M0 |= 0x3F;
    P2M1 &= ~0x3F;

    /* P0.0~P0.5 quasi inputs with pull-up */
    P0M0 &= ~0x3F;
    P0M1 &= ~0x3F;
    P0 |= 0x3F;

    /* all CLK low, default DIR high */
    P2 &= (unsigned char)(~0x3F);
    P2 |= (DIR_X_BIT | DIR_Y_BIT | DIR_Z_BIT);
}

static bit key_pressed(unsigned char mask)
{
    return (P0 & mask) ? 0 : 1;
}

static void set_axis_dir_x(bit forward)
{
    if (forward) P2 |= DIR_X_BIT;
    else         P2 &= (unsigned char)(~DIR_X_BIT);
}

static void set_axis_dir_y(bit forward)
{
    if (forward) P2 |= DIR_Y_BIT;
    else         P2 &= (unsigned char)(~DIR_Y_BIT);
}

static void set_axis_dir_z(bit forward)
{
    if (forward) P2 |= DIR_Z_BIT;
    else         P2 &= (unsigned char)(~DIR_Z_BIT);
}

static void clear_clk(void)
{
    P2 &= (unsigned char)(~CLK_MASK);
    clk_level = 0;
    acc_x = acc_y = acc_z = 0;
    inc_x = inc_y = inc_z = 0;
}

static void base_delay(void)
{
    unsigned int i;
    for (i = 0; i < BASE_DELAY_COUNT; i++)
    {
    }
}

static void delay_ms(unsigned int ms)
{
    unsigned int a, b;
    for (a = 0; a < ms; a++)
    {
        for (b = 0; b < 800; b++)
        {
        }
    }
}

static bit any_replay_remaining(void)
{
    return (play_x_pos > 0 || play_x_neg > 0 ||
            play_y_pos > 0 || play_y_neg > 0 ||
            play_z_pos > 0 || play_z_neg > 0);
}

/*
 * Linear accel/decel ramp of one axis speed toward 'target'.
 *   target >= START_INCR : keep moving (accelerate up / hold / ease down to it)
 *   target == 0          : decelerate down to start speed, then stop
 * Start speed is never 0 (指导书: 起始速度=极限起动频率).
 */
static unsigned int ramp(unsigned int cur, unsigned int target)
{
    if (target > 0)
    {
        if (cur < START_INCR)            cur = START_INCR;      /* kick-start */
        else if (cur < target)
        {
            cur += ACCEL_STEP;
            if (cur > target) cur = target;
        }
        else if (cur > target)
        {
            cur -= ACCEL_STEP;
            if (cur < target)     cur = target;
            if (cur < START_INCR) cur = START_INCR;
        }
    }
    else
    {
        if (cur > START_INCR)
        {
            cur -= ACCEL_STEP;
            if (cur < START_INCR) cur = START_INCR;
        }
        else
        {
            cur = 0;                     /* reached start speed -> stop */
        }
    }
    return cur;
}

/*
 * Advance the DDS by one base tick using the current per-axis speeds
 * (inc_x/y/z). An axis with speed 0 is held low. Returns a bitmask of the
 * axes whose CLK just went LOW->HIGH (i.e. one motor step happened).
 */
static unsigned char dds_tick(void)
{
    unsigned char rising = 0;

    if (inc_x)
    {
        acc_x += inc_x;
        if (acc_x >= F_BASE_HZ)
        {
            acc_x -= F_BASE_HZ;
            if (clk_level & X_CLK_BIT) clk_level &= (unsigned char)(~X_CLK_BIT);
            else { clk_level |= X_CLK_BIT; rising |= X_CLK_BIT; }
        }
    }
    else { clk_level &= (unsigned char)(~X_CLK_BIT); acc_x = 0; }

    if (inc_y)
    {
        acc_y += inc_y;
        if (acc_y >= F_BASE_HZ)
        {
            acc_y -= F_BASE_HZ;
            if (clk_level & Y_CLK_BIT) clk_level &= (unsigned char)(~Y_CLK_BIT);
            else { clk_level |= Y_CLK_BIT; rising |= Y_CLK_BIT; }
        }
    }
    else { clk_level &= (unsigned char)(~Y_CLK_BIT); acc_y = 0; }

    if (inc_z)
    {
        acc_z += inc_z;
        if (acc_z >= F_BASE_HZ)
        {
            acc_z -= F_BASE_HZ;
            if (clk_level & Z_CLK_BIT) clk_level &= (unsigned char)(~Z_CLK_BIT);
            else { clk_level |= Z_CLK_BIT; rising |= Z_CLK_BIT; }
        }
    }
    else { clk_level &= (unsigned char)(~Z_CLK_BIT); acc_z = 0; }

    /* write CLK bits, keep DIR bits untouched */
    P2 = (unsigned char)((P2 & (unsigned char)(~CLK_MASK)) | (clk_level & CLK_MASK));

    base_delay();
    return rising;
}

/* small fixed +Z positioning move at start of a scan, run at start speed
 * (crawl) so the pulse count is exact. */
static void pre_move_z_on_scan_start(void)
{
    i32 done = 0;

    clear_clk();
    inc_z = START_INCR;
    while (done < SCAN_Z_PREMOVE_PULSES)
    {
        if (dds_tick() & Z_CLK_BIT)
            done++;
    }
    clear_clk();
}

void main(void)
{
    bit latch_scan = 0;
    bit latch_stop = 0;

    IO_Init();

    while (1)
    {
        bit dir_pressed = key_pressed(0x01);  /* P0.0 */
        bit x_pressed   = key_pressed(0x02);  /* P0.1 */
        bit y_pressed   = key_pressed(0x04);  /* P0.2 */
        bit z_pressed   = key_pressed(0x08);  /* P0.3 */
        bit stop_pressed= key_pressed(0x10);  /* P0.4 */
        bit scan_pressed= key_pressed(0x20);  /* P0.5 */

        bit scan_event = 0;
        bit stop_event = 0;

        unsigned char rising;
        bit dir_manual_forward = dir_pressed ? 0 : 1;

        /* per-axis speed targets this tick (0 == command to stop) */
        unsigned int tx, ty, tz;
        i32 segx, segy, segz;

        if (scan_pressed && !latch_scan)
        {
            delay_ms(KEY_DEBOUNCE_MS);
            if (key_pressed(0x20)) { scan_event = 1; latch_scan = 1; }
        }
        else if (!scan_pressed && latch_scan)
        {
            delay_ms(KEY_DEBOUNCE_MS);
            if (!key_pressed(0x20)) latch_scan = 0;
        }

        if (stop_pressed && !latch_stop)
        {
            delay_ms(KEY_DEBOUNCE_MS);
            if (key_pressed(0x10)) { stop_event = 1; latch_stop = 1; }
        }
        else if (!stop_pressed && latch_stop)
        {
            delay_ms(KEY_DEBOUNCE_MS);
            if (!key_pressed(0x10)) latch_stop = 0;
        }

        if (scan_event && motion_state == 0)
        {
            /* reset record and start scan */
            rec_x_pos = rec_x_neg = 0;
            rec_y_pos = rec_y_neg = 0;
            rec_z_pos = rec_z_neg = 0;
            play_x_pos = play_x_neg = 0;
            play_y_pos = play_y_neg = 0;
            play_z_pos = play_z_neg = 0;

            /* scan starts with a small +Z pre-move and record it */
            set_axis_dir_z(1);
            pre_move_z_on_scan_start();
            rec_z_pos += SCAN_Z_PREMOVE_PULSES;

            motion_state = 1;
            scan_stop_req = 0;
        }

        /* global 3-state stop/replay button:
         * scan running -> request controlled stop (X ramps down);
         * idle         -> load & start replay. */
        if (stop_event)
        {
            if (motion_state == 1)
            {
                scan_stop_req = 1;
            }
            else if (motion_state == 0)
            {
                play_x_neg = rec_x_pos;
                play_x_pos = rec_x_neg;
                play_y_neg = rec_y_pos;
                play_y_pos = rec_y_neg;
                play_z_neg = rec_z_pos;
                play_z_pos = rec_z_neg;
                motion_state = 2;
            }
        }

        if (motion_state == 2)
        {
            /* ---- REPLAY: known distance, trapezoid with planned decel ---- *
             * accelerate -> cruise -> when remaining <= DECEL_RESERVE ease
             * down to start speed and crawl -> hard stop exactly at 0 steps
             * (crawl speed is safe to stop from, so no lost/extra steps).   */
            if (play_x_neg > 0) { set_axis_dir_x(0); segx = play_x_neg; }
            else if (play_x_pos > 0) { set_axis_dir_x(1); segx = play_x_pos; }
            else segx = 0;

            if (play_y_neg > 0) { set_axis_dir_y(0); segy = play_y_neg; }
            else if (play_y_pos > 0) { set_axis_dir_y(1); segy = play_y_pos; }
            else segy = 0;

            if (play_z_neg > 0) { set_axis_dir_z(0); segz = play_z_neg; }
            else if (play_z_pos > 0) { set_axis_dir_z(1); segz = play_z_pos; }
            else segz = 0;

            if (segx == 0) inc_x = 0;
            else { tx = (segx > DECEL_RESERVE) ? CRUISE_X : START_INCR; inc_x = ramp(inc_x, tx); }

            if (segy == 0) inc_y = 0;
            else { ty = (segy > DECEL_RESERVE) ? CRUISE_Y : START_INCR; inc_y = ramp(inc_y, ty); }

            if (segz == 0) inc_z = 0;
            else { tz = (segz > DECEL_RESERVE) ? CRUISE_Z : START_INCR; inc_z = ramp(inc_z, tz); }
        }
        else
        {
            /* ---- SCAN(state 1) or IDLE(state 0): button-driven trapezoid --- *
             * press  -> target = cruise  (accelerate, 梯形左下角起)             *
             * release-> target = 0       (decelerate to start speed then stop, *
             *                             右斜坡, 梯形右上角起)                 */
            if (motion_state == 1) set_axis_dir_x(SCAN_X_FORWARD);
            else                   set_axis_dir_x(dir_manual_forward);
            set_axis_dir_y(dir_manual_forward);
            set_axis_dir_z(dir_manual_forward);

            if (motion_state == 1)
            {
                tx = scan_stop_req ? 0 : CRUISE_X;   /* X cruises until stop requested */
                ty = y_pressed ? CRUISE_Y : 0;       /* Y/Z still hand-jog-able */
                tz = z_pressed ? CRUISE_Z : 0;
            }
            else /* idle manual jog */
            {
                tx = x_pressed ? CRUISE_X : 0;
                ty = y_pressed ? CRUISE_Y : 0;
                tz = z_pressed ? CRUISE_Z : 0;
            }

            inc_x = ramp(inc_x, tx);
            inc_y = ramp(inc_y, ty);
            inc_z = ramp(inc_z, tz);
        }

        /* generate one base tick of pulses at the current per-axis speeds */
        rising = dds_tick();

        /* count one motor step per rising edge, per axis */
        if (motion_state == 2)
        {
            if (rising & X_CLK_BIT)
            {
                if (play_x_neg > 0) play_x_neg--;
                else if (play_x_pos > 0) play_x_pos--;
            }
            if (rising & Y_CLK_BIT)
            {
                if (play_y_neg > 0) play_y_neg--;
                else if (play_y_pos > 0) play_y_pos--;
            }
            if (rising & Z_CLK_BIT)
            {
                if (play_z_neg > 0) play_z_neg--;
                else if (play_z_pos > 0) play_z_pos--;
            }

            if (!any_replay_remaining())
            {
                motion_state = 0;
                clear_clk();
            }
        }
        else if (motion_state == 1)
        {
            /* record every actual step (incl. accel/decel tails) so replay
             * reverses to the exact origin */
            if (rising & X_CLK_BIT)
            {
                if (SCAN_X_FORWARD) rec_x_pos++;
                else rec_x_neg++;
            }
            if (rising & Y_CLK_BIT)
            {
                if (dir_manual_forward) rec_y_pos++;
                else rec_y_neg++;
            }
            if (rising & Z_CLK_BIT)
            {
                if (dir_manual_forward) rec_z_pos++;
                else rec_z_neg++;
            }

            /* finish the scan only after X (and any jog) has fully ramped
             * down to a stop, so no un-recorded steps escape */
            if (scan_stop_req && inc_x == 0 && inc_y == 0 && inc_z == 0)
            {
                motion_state = 0;
                scan_stop_req = 0;
                clear_clk();
            }
        }
    }
}
