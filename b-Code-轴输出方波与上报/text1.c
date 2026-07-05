#include "STC8A8K64D4.H"
#include <intrins.h>

void UART_Init(void)
{
    P_SW2 |= 0x80;
    SCON = 0x50;
    AUXR |= 0x40;
    AUXR &= ~0x01;
    TMOD &= 0x0F;
    TMOD |= 0x20;
    TH1 = 0xFD;      // 9600bps
    TL1 = 0xFD;
    TR1 = 1;
    TI = 1;
}

void UART_SendChar(unsigned char ch)
{
    SBUF = ch;
    while (!TI);
    TI = 0;
}

void IO_init(void)
{
    P_SW2 |= 0x80;
    P2M0 |= 0x01;
    P2M1 &= ~0x01;
}

void main(void)
{
    unsigned int i;
    
    IO_init();
    UART_Init();
    
    while (1)
    {
        P2 ^= 0x01;                    // 翻转 P2.0 产生方波
        
        if (P2 & 0x01)
            UART_SendChar('H');
        else
            UART_SendChar('L');
        
        for(i = 0; i < 2100; i++);     // 延时控制频率（约200Hz）
    }
}