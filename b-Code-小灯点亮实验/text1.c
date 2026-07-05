#include "STC8A8K64D4.H"
#include <intrins.h>

void MAX7219_WriteByte(unsigned char dat);
void MAX7219_Write(unsigned char addr, unsigned char dat);
void MAX7219_Display_Graph(unsigned char *graph);

// ? 把图形数组定义为全局变量，去掉 const，放在函数外面
unsigned char heart[8] = {
    0x18, 0x3C, 0x7E, 0xFF,
    0xFF, 0x7E, 0x3C, 0x18
};

// 可选：笑脸图案，切换注释即可
// unsigned char smile[8] = {
//     0x3C, 0x42, 0xA5, 0x81,
//     0xA5, 0x99, 0x42, 0x3C
// };

void IO_init(void)
{
    P_SW2 |= 0x80;   // 开启扩展寄存器访问
    P2M0 |= 0x07;    // P2.0(DIN) P2.1(CS) P2.2(CLK) 推挽输出
    P2M1 &= ~0x07;
}

void MAX7219_WriteByte(unsigned char dat)
{
    unsigned char i;
    for(i = 0; i < 8; i++)
    {
        P2 &= ~0x04;         // CLK拉低
        if(dat & 0x80) P2 |= 0x01;
        else           P2 &= ~0x01;
        dat <<= 1;
        P2 |= 0x04;          // CLK拉高锁存
    }
}

void MAX7219_Write(unsigned char addr, unsigned char dat)
{
    P2 &= ~0x02;             // CS拉低
    MAX7219_WriteByte(addr);
    MAX7219_WriteByte(dat);
    P2 |= 0x02;              // CS拉高锁存
}

void MAX7219_Init(void)
{
    MAX7219_Write(0x0C, 0x01);  // 开启显示
    MAX7219_Write(0x0B, 0x07);  // 扫描8行
    MAX7219_Write(0x09, 0x00);  // 不译码，适合点阵
    MAX7219_Write(0x0A, 0x08);  // 亮度0~15
    MAX7219_Write(0x0F, 0x00);  // 关闭测试模式
}

void MAX7219_Clear(void)
{
    unsigned char i;
    for(i = 1; i <= 8; i++) MAX7219_Write(i, 0x00);
}

void MAX7219_Display_Graph(unsigned char *graph)
{
    unsigned char i;
    for(i = 0; i < 8; i++)
    {
        MAX7219_Write(i + 1, graph[i]);
    }
}

void delay_ms(unsigned int ms)
{
    unsigned int a, b;
    for(a = 0; a < ms; a++)
        for(b = 0; b < 800; b++); // 适配11.0592MHz / 12MHz晶振
}

void main(void)
{
    IO_init();
    MAX7219_Init();
    MAX7219_Clear();

    while(1)
    {
        MAX7219_Display_Graph(heart);  // 显示心形
        delay_ms(1000);
        MAX7219_Clear();
        delay_ms(500);
    }
}