


/*
   闪烁灯实验 
   运动指示灯直接接在P55口上
   只要控制io口相应电平变化即可
   */



#include "STC8A8K64D4.H"



/******
功能：IO口配置
输入：无
输出：无
*****/
void IO_init(void)
{
  P0M0 = 0X00;
  P0M1 = 0X00;

  P1M0 = 0X00;
  P1M1 = 0X00;

  P2M0 = 0X00;
  P2M1 = 0X00;

  P3M0 = 0X00;
  P3M1 = 0X00;

  P4M0 = 0X00;
  P4M1 = 0X00;

  P5M0 = 0X00;
  P5M1 = 0X00;

  P6M0 = 0X00;
  P6M1 = 0X00;

  P7M0 = 0X00;
  P7M1 = 0X00;

//  P_SW2=0x00;   //串口选RxD_2，TXD_2
//	RW_S = 0x40;  //总线控制选RD_2，WR_2
 
}

void delayms(unsigned int m)
   {
	  int  a, b;

	 for(a=0;a<5000;a++)
	 for(b=0;b<m;b++);
	   
	 }



main()
{
	IO_init();
	while(1)
		{
			P55=0;
			delayms(200);
			P55=1;
			delayms(200);

		}
}

