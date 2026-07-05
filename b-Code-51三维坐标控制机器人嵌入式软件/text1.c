/*
 * STC8A8K64D4 3-axis Cartesian robot controller
 * Protocol:
 *   PC -> MCU: Fx,Fy,Fz,DirX,DirY,DirZ###  (new)
 *   PC -> MCU: Fx,Fy,Fz###                  (legacy, dir defaults to 1,1,1)
 *   MCU -> PC: OK###
 *   Test:      PING### -> PONG###
 */
#include "STC8A8K64D4.H"

sbit CLK_X = P2^0;
sbit DIR_X = P2^1;
sbit CLK_Y = P2^2;
sbit DIR_Y = P2^3;
sbit CLK_Z = P2^4;
sbit DIR_Z = P2^5;
sbit LED_STATUS = P5^5;

#define FOSC            11059200UL
#define BAUD            9600UL
#define BUF_SIZE        64
#define TICK_HZ         10000UL
#define TIMER0_RELOAD   (65536UL - FOSC / 12UL / TICK_HZ)
#define SEGMENT_RUN_MS  1000
#define SEGMENT_TICKS   (SEGMENT_RUN_MS * (TICK_HZ / 1000))
#define CLK_X_MASK      0x01

static unsigned char xdata recv_buf[BUF_SIZE];
static unsigned char recv_cnt = 0;

static unsigned int active_Fx = 0;
static unsigned int active_Fy = 0;
static unsigned int active_Fz = 0;
static unsigned char pulse_enable = 0;

static unsigned int x_toggle_cnt = 0;
static unsigned int y_toggle_cnt = 0;
static unsigned int z_toggle_cnt = 0;
static unsigned int x_half_period = 1;
static unsigned int y_half_period = 1;
static unsigned int z_half_period = 1;

static volatile unsigned int segment_ticks_left = 0;
static volatile unsigned char segment_done = 0;

static void IO_Init(void);
static void UART_Init(void);
static void Timer0_Init(void);
static void UART_SendByte(unsigned char dat);
static void UART_SendString(const char code *s);
static unsigned char StrEq(const char *line, const char code *text);
static unsigned char ParseCommandLine(
    const char *line,
    unsigned int *fx,
    unsigned int *fy,
    unsigned int *fz,
    unsigned char *dirX,
    unsigned char *dirY,
    unsigned char *dirZ);
static void LoadPulses(unsigned int fx, unsigned int fy, unsigned int fz);
static void StopPulses(void);
static void StartSegmentRun(void);
static void WaitSegmentDone(void);
static void RunSegment(unsigned int fx, unsigned int fy, unsigned int fz, unsigned char dirX, unsigned char dirY, unsigned char dirZ);
static void ProcessFrame(unsigned char len);
static unsigned char FrameEndsWithHash(void);

static unsigned char ParseUnsignedField(const char *line, unsigned char *idx, unsigned int *value)
{
    unsigned int v = 0;
    unsigned char digits = 0;

    while (line[*idx] >= '0' && line[*idx] <= '9')
    {
        v = (unsigned int)(v * 10 + (line[*idx] - '0'));
        (*idx)++;
        digits++;
    }

    if (digits == 0)
        return 0;

    *value = v;
    return 1;
}

static unsigned char ParseDirField(const char *line, unsigned char *idx, unsigned char *value)
{
    if (line[*idx] == '0')
    {
        *value = 0;
        (*idx)++;
        return 1;
    }

    if (line[*idx] == '1')
    {
        *value = 1;
        (*idx)++;
        return 1;
    }

    return 0;
}

static void UART_SendByte(unsigned char dat)
{
    SBUF = dat;
    while (!TI);
    TI = 0;
}

static void UART_SendString(const char code *s)
{
    while (*s)
        UART_SendByte((unsigned char)*s++);
}

static void IO_Init(void)
{
    P_SW2 |= 0x80;

    P2M0 |= 0x3F;
    P2M1 &= ~0x3F;

    P5M0 |= 0x20;
    P5M1 &= ~0x20;

    P3M0 |= 0x02;
    P3M1 &= ~0x03;

    CLK_X = 0;
    CLK_Y = 0;
    CLK_Z = 0;
    DIR_X = 1;
    DIR_Y = 1;
    DIR_Z = 1;
    LED_STATUS = 0;
}

static void UART_Init(void)
{
    P_SW1 &= ~0xC0;
    SCON = 0x50;
    AUXR &= ~0x04;
    AUXR &= ~0x01;
    TMOD &= 0x0F;
    TMOD |= 0x20;
    TH1 = (unsigned char)(256UL - FOSC / 12UL / 32UL / BAUD);
    TL1 = TH1;
    TR1 = 1;
    ES = 0;
    TI = 1;
}

static void Timer0_Init(void)
{
    TMOD &= 0xF0;
    TMOD |= 0x01;
    TH0 = (unsigned char)(TIMER0_RELOAD >> 8);
    TL0 = (unsigned char)TIMER0_RELOAD;
    ET0 = 1;
    TR0 = 0;
}

static unsigned char StrEq(const char *line, const char code *text)
{
    while (*line != '\0' && *text != '\0')
    {
        if (*line != *text)
            return 0;
        line++;
        text++;
    }
    return (*line == *text);
}

static unsigned char ParseCommandLine(
    const char *line,
    unsigned int *fx,
    unsigned int *fy,
    unsigned int *fz,
    unsigned char *dirX,
    unsigned char *dirY,
    unsigned char *dirZ)
{
    unsigned char i = 0;
    unsigned int x, y, z;
    unsigned char dx = 1, dy = 1, dz = 1;

    while (line[i] == ' ' || line[i] == '\t')
        i++;

    if (!ParseUnsignedField(line, &i, &x)) return 0;
    if (line[i++] != ',') return 0;
    if (!ParseUnsignedField(line, &i, &y)) return 0;
    if (line[i++] != ',') return 0;
    if (!ParseUnsignedField(line, &i, &z)) return 0;

    while (line[i] == ' ' || line[i] == '\t')
        i++;

    if (line[i] == ',')
    {
        i++;
        while (line[i] == ' ' || line[i] == '\t') i++;
        if (!ParseDirField(line, &i, &dx)) return 0;

        while (line[i] == ' ' || line[i] == '\t') i++;
        if (line[i++] != ',') return 0;

        while (line[i] == ' ' || line[i] == '\t') i++;
        if (!ParseDirField(line, &i, &dy)) return 0;

        while (line[i] == ' ' || line[i] == '\t') i++;
        if (line[i++] != ',') return 0;

        while (line[i] == ' ' || line[i] == '\t') i++;
        if (!ParseDirField(line, &i, &dz)) return 0;
    }

    while (line[i] == ' ' || line[i] == '\t')
        i++;
    if (line[i] != '\0') return 0;

    if (x > 20000 || y > 20000 || z > 20000) return 0;

    *fx = x;
    *fy = y;
    *fz = z;
    *dirX = dx;
    *dirY = dy;
    *dirZ = dz;
    return 1;
}

static void LoadPulses(unsigned int fx, unsigned int fy, unsigned int fz)
{
    active_Fx = fx;
    active_Fy = fy;
    active_Fz = fz;

    x_toggle_cnt = 0;
    y_toggle_cnt = 0;
    z_toggle_cnt = 0;

    x_half_period = (active_Fx > 0) ? (unsigned int)(TICK_HZ / (2UL * active_Fx)) : 1;
    y_half_period = (active_Fy > 0) ? (unsigned int)(TICK_HZ / (2UL * active_Fy)) : 1;
    z_half_period = (active_Fz > 0) ? (unsigned int)(TICK_HZ / (2UL * active_Fz)) : 1;

    if (x_half_period == 0) x_half_period = 1;
    if (y_half_period == 0) y_half_period = 1;
    if (z_half_period == 0) z_half_period = 1;
}

static void StopPulses(void)
{
    pulse_enable = 0;
    TR0 = 0;
    CLK_X = 0;
    CLK_Y = 0;
    CLK_Z = 0;
}

static void StartSegmentRun(void)
{
    segment_done = 0;
    segment_ticks_left = (unsigned int)SEGMENT_TICKS;
    pulse_enable = 1;
    TR0 = 1;
}

static void WaitSegmentDone(void)
{
    while (!segment_done)
    {
    }
    StopPulses();
}

static void RunSegment(unsigned int fx, unsigned int fy, unsigned int fz, unsigned char dirX, unsigned char dirY, unsigned char dirZ)
{
    LED_STATUS = 1;

    DIR_X = dirX ? 1 : 0;
    DIR_Y = dirY ? 1 : 0;
    DIR_Z = dirZ ? 1 : 0;

    LoadPulses(fx, fy, fz);
    StartSegmentRun();
    WaitSegmentDone();
    LED_STATUS = 0;
}

static unsigned char FrameEndsWithHash(void)
{
    if (recv_cnt < 3)
        return 0;
    return (recv_buf[recv_cnt - 3] == '#' &&
            recv_buf[recv_cnt - 2] == '#' &&
            recv_buf[recv_cnt - 1] == '#');
}

static void ProcessFrame(unsigned char len)
{
    unsigned int fx, fy, fz;
    unsigned char dirX, dirY, dirZ;

    recv_buf[len] = '\0';

    if (StrEq((const char *)recv_buf, "PING"))
    {
        UART_SendString("PONG###");
        return;
    }

    if (ParseCommandLine((const char *)recv_buf, &fx, &fy, &fz, &dirX, &dirY, &dirZ))
    {
        RunSegment(fx, fy, fz, dirX, dirY, dirZ);
        UART_SendString("OK###");
    }
}

void Timer0_ISR(void) interrupt 1
{
    TH0 = (unsigned char)(TIMER0_RELOAD >> 8);
    TL0 = (unsigned char)TIMER0_RELOAD;

    if (!pulse_enable) return;

    if (active_Fx > 0)
    {
        x_toggle_cnt++;
        if (x_toggle_cnt >= x_half_period)
        {
            x_toggle_cnt = 0;
            P2 ^= CLK_X_MASK; /* match validated 200Hz toggle style on P2.0 */
        }
    }

    if (active_Fy > 0)
    {
        y_toggle_cnt++;
        if (y_toggle_cnt >= y_half_period)
        {
            y_toggle_cnt = 0;
            CLK_Y = !CLK_Y;
        }
    }

    if (active_Fz > 0)
    {
        z_toggle_cnt++;
        if (z_toggle_cnt >= z_half_period)
        {
            z_toggle_cnt = 0;
            CLK_Z = !CLK_Z;
        }
    }

    if (segment_ticks_left > 0)
    {
        segment_ticks_left--;
        if (segment_ticks_left == 0)
            segment_done = 1;
    }
}

void main(void)
{
    IO_Init();
    UART_Init();
    Timer0_Init();
    EA = 1;

    while (1)
    {
        if (RI)
        {
            RI = 0;

            if (recv_cnt < BUF_SIZE)
            {
                recv_buf[recv_cnt++] = SBUF;

                if (FrameEndsWithHash())
                {
                    recv_cnt -= 3;
                    ProcessFrame(recv_cnt);
                    recv_cnt = 0;
                }
            }
            else
            {
                recv_cnt = 0;
            }
        }
    }
}
