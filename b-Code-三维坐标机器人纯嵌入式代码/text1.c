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

/* keep same style as your stable reference and tune for ~1200Hz */
#define HALF_DELAY_COUNT 350
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
static void phase_delay(void);
static void delay_ms(unsigned int ms);
static bit any_replay_remaining(void);
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
}

static void phase_delay(void)
{
    unsigned int i;
    for (i = 0; i < HALF_DELAY_COUNT; i++)
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

static void pre_move_z_on_scan_start(void)
{
    i32 i;
    for (i = 0; i < SCAN_Z_PREMOVE_PULSES; i++)
    {
        /* one full pulse period on Z only */
        clear_clk();
        P2 |= Z_CLK_BIT;
        phase_delay();

        clear_clk();
        phase_delay();
    }
}

void main(void)
{
    bit phase_high = 0;

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

        unsigned char clk_out = 0;
        bit dir_manual_forward = dir_pressed ? 0 : 1;

        if (scan_pressed && !latch_scan)
        {
            delay_ms(KEY_DEBOUNCE_MS);
            if (key_pressed(0x20))
            {
                scan_event = 1;
                latch_scan = 1;
            }
        }
        else if (!scan_pressed && latch_scan)
        {
            delay_ms(KEY_DEBOUNCE_MS);
            if (!key_pressed(0x20))
                latch_scan = 0;
        }

        if (stop_pressed && !latch_stop)
        {
            delay_ms(KEY_DEBOUNCE_MS);
            if (key_pressed(0x10))
            {
                stop_event = 1;
                latch_stop = 1;
            }
        }
        else if (!stop_pressed && latch_stop)
        {
            delay_ms(KEY_DEBOUNCE_MS);
            if (!key_pressed(0x10))
                latch_stop = 0;
        }

        if (scan_event && motion_state != 1)
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
            phase_high = 0;
            clear_clk();
        }

        /* global 3-state stop/replay button:
         * state 1 -> 0 (stop), then state 0 -> 2 (replay) */
        if (stop_event)
        {
            if (motion_state == 1)
            {
                motion_state = 0;
                phase_high = 0;
                clear_clk();
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
                phase_high = 0;
                clear_clk();
            }
        }

        if (motion_state == 2)
        {
            /* set per-axis replay direction and choose active axes */
            if (play_x_neg > 0) { set_axis_dir_x(0); clk_out |= X_CLK_BIT; }
            else if (play_x_pos > 0) { set_axis_dir_x(1); clk_out |= X_CLK_BIT; }

            if (play_y_neg > 0) { set_axis_dir_y(0); clk_out |= Y_CLK_BIT; }
            else if (play_y_pos > 0) { set_axis_dir_y(1); clk_out |= Y_CLK_BIT; }

            if (play_z_neg > 0) { set_axis_dir_z(0); clk_out |= Z_CLK_BIT; }
            else if (play_z_pos > 0) { set_axis_dir_z(1); clk_out |= Z_CLK_BIT; }
        }
        else
        {
            /* manual directions for Y/Z, X direction overridden when scanning */
            set_axis_dir_y(dir_manual_forward);
            set_axis_dir_z(dir_manual_forward);
            if (motion_state == 1) set_axis_dir_x(SCAN_X_FORWARD);
            else set_axis_dir_x(dir_manual_forward);

            if (motion_state == 1)
            {
                /* during scan X always moves +, others still manually operable */
                clk_out |= X_CLK_BIT;
                if (y_pressed) clk_out |= Y_CLK_BIT;
                if (z_pressed) clk_out |= Z_CLK_BIT;
            }
            else
            {
                /* idle manual jog */
                if (x_pressed) clk_out |= X_CLK_BIT;
                if (y_pressed) clk_out |= Y_CLK_BIT;
                if (z_pressed) clk_out |= Z_CLK_BIT;
            }
        }

        if (clk_out == 0)
        {
            phase_high = 0;
            clear_clk();
            continue;
        }

        phase_high = !phase_high;
        clear_clk();

        if (phase_high)
        {
            P2 |= clk_out;

            /* count one pulse on rising phase */
            if (motion_state == 2)
            {
                if ((clk_out & X_CLK_BIT) != 0)
                {
                    if (play_x_neg > 0) play_x_neg--;
                    else if (play_x_pos > 0) play_x_pos--;
                }
                if ((clk_out & Y_CLK_BIT) != 0)
                {
                    if (play_y_neg > 0) play_y_neg--;
                    else if (play_y_pos > 0) play_y_pos--;
                }
                if ((clk_out & Z_CLK_BIT) != 0)
                {
                    if (play_z_neg > 0) play_z_neg--;
                    else if (play_z_pos > 0) play_z_pos--;
                }

                if (!any_replay_remaining())
                {
                    motion_state = 0;
                    phase_high = 0;
                    clear_clk();
                }
            }
            else if (motion_state == 1)
            {
                if ((clk_out & X_CLK_BIT) != 0)
                {
                    if (SCAN_X_FORWARD) rec_x_pos++;
                    else rec_x_neg++;
                }
                if ((clk_out & Y_CLK_BIT) != 0)
                {
                    if (dir_manual_forward) rec_y_pos++;
                    else rec_y_neg++;
                }
                if ((clk_out & Z_CLK_BIT) != 0)
                {
                    if (dir_manual_forward) rec_z_pos++;
                    else rec_z_neg++;
                }
            }
        }

        phase_delay();
    }
}
