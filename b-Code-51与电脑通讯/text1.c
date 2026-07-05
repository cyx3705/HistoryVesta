// STC8A8K64D4 串口协议处理程序
// 通信规则：数据以 ### 结尾，收到完整帧再转发
// 时钟：11.064MHz  波特率：9600 8N1
#include <reg51.h>

sfr AUXR  = 0x8E;
sfr P_SW1 = 0xA2;

#define FOSC        11064000UL   // 芯片校准后真实频率
#define BAUD        9600UL        // 波特率
#define BUF_SIZE    64            // 单帧最大长度，可根据需求增大

unsigned char recv_buf[BUF_SIZE];  // 接收缓冲区
unsigned char recv_cnt = 0;        // 接收计数

void UART_Init(void);
void Send_Byte(unsigned char dat);
void Send_Str(const char *str);
void Send_Buf(unsigned char *buf, unsigned char len);
void Delay_ms(unsigned int ms);

void main(void)
{
    UART_Init();
    Send_Str("\r\n=== 串口协议就绪 ===\r\n");
    Send_Str("格式：内容 + ### 结束\r\n\r\n");

    while(1)
    {
        if(RI)
        {
            RI = 0;
            if(recv_cnt < BUF_SIZE)
            {
                recv_buf[recv_cnt++] = SBUF;

                // 检测结束标记 ###
                if(recv_cnt >= 3 &&
                   recv_buf[recv_cnt-3] == '#' &&
                   recv_buf[recv_cnt-2] == '#' &&
                   recv_buf[recv_cnt-1] == '#')
                {
                    recv_cnt -= 3;  // 去掉结束符

                    Send_Str("[收到完整帧] 长度=");
                    Send_Byte((recv_cnt / 10) + '0');
                    Send_Byte((recv_cnt % 10) + '0');
                    Send_Str(" 内容：");
                    Send_Buf(recv_buf, recv_cnt);
                    Send_Str("\r\n[转发完成]\r\n\r\n");

                    recv_cnt = 0;   // 清空缓冲区，准备下一帧
                }
            }
            else
            {
                recv_cnt = 0;
                Send_Str("[警告] 数据超出缓冲区，已重置\r\n\r\n");
            }
        }
        else
        {
            // 每2秒发送一次心跳，确认程序运行正常
            static unsigned int t = 0;
            if(++t >= 2000)
            {
                t = 0;
                Send_Str("[心跳] 运行正常\r\n");
            }
            Delay_ms(1);
        }
    }
}

void UART_Init(void)
{
    P_SW1 &= ~0xC0;                // 固定映射到 P3.0(RXD) / P3.1(TXD)
    SCON  = 0x50;                  // 模式1，8位数据，允许接收
    AUXR &= ~0x04;                 // 定时器1时钟 = FOSC / 12
    AUXR &= ~0x01;                 // 关闭1T模式，保证配置匹配
    TMOD &= 0x0F;
    TMOD |= 0x20;                  // 定时器1 8位自动重装
    TH1 = (unsigned char)(256UL - FOSC / 12UL / 32UL / BAUD);
    TL1 = TH1;
    TR1 = 1;                       // 启动定时器1
}

void Send_Byte(unsigned char dat)
{
    SBUF = dat;
    while(!TI);
    TI = 0;
}

void Send_Str(const char *str)
{
    while(*str) Send_Byte((unsigned char)*str++);
}

void Send_Buf(unsigned char *buf, unsigned char len)
{
    unsigned char i;
    for(i = 0; i < len; i++)
        Send_Byte(buf[i]);
}

void Delay_ms(unsigned int ms)
{
    unsigned int i, j;
    for(i = ms; i > 0; i--)
        for(j = 960; j > 0; j--);
}