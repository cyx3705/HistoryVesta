syms t
%t=0:0.01:pi;
a=0.4;
l=0.8;
%y=0.99 *sin(t + 1.4);
%x=0.3960* cos(2.65*(t + 1.4));
x=-2*a* ((l - cos (t)) .*cos( t) + (1 - l));
y=2*a*(l -cos (t)).* sin(t);
 x_1=diff(x,t);
 x_2=diff(x,t,2);%一阶

 y_1=diff(y,t);
 y_2=diff(y,t,2);%二阶

 tant=-x_1/y_1;

x_ = @(t) -2 * 0.4 * ((0.4 - cos(t)) .* cos(t) + (1 - 0.4));
y_ = @(t) 2 * 0.4 * (0.4 - cos(t)) .* sin(t);%内联函数

%x_=inline(' (99*cos((53*t)/20 + 371/100))/250','t');
%y_=inline('(99*sin(t + 7/5))/100','t');%内联函数

poall_=[];%圆心集合
po=[];%圆心
rtheat=[]; %圆心角rtheat
r=[];%半径r
v1=[];%v1右轮近圆
v2=[];%v2左轮远圆
%求速度，半径r，圆心角rtheat,圆心po，v1右轮近圆，v2左轮远圆
for i=0:15
[rtheat(i+1),r(i+1),v1(i+1),v2(i+1),po]=points2circle([x_(i*pi/15),y_(i*pi/15)],[x_(i*pi/15+pi/30),y_(i*pi/15+pi/30)],[x_((i+1)*pi/15),y_((i+1)*pi/15)]);
%15段，每段两侧端点以及中间点
poall_=[poall_,po];
end;

function [realangle,r,v1,v2,pc]=points2circle(p1,p2,p3)
    % 输入检查
    validateattributes(p1,{'numeric'},{'row'},1);% 行向量
    validateattributes(p2,{'numeric'},{'row'},2);
    validateattributes(p3,{'numeric'},{'row'},3);
    num1=length(p1);num2=length(p2);num3=length(p3);
    if (num1 == num2) && (num2 == num3)
        if num1 == 2
            p1=[p1,0];p2=[p2,0];p3=[p3,0];
        elseif num1 ~= 3
            error('仅支持二维或三维坐标输入');
        end
    else
        error('输入坐标的维数不一致');
    end
    % 共线检查
    temp01=p1-p2;temp02=p3-p2;
    temp03=cross(temp01,temp02);
    temp=(temp03*temp03')/(temp01*temp01')/(temp02*temp02');
    if temp < 10^-6
        error('三点共线, 无法确定圆');
    end
    mat1=[p1,1;p2,1;p3,1];% size = 3x4
    m=+det(mat1(:,2:4));
    n=-det([mat1(:,1),mat1(:,3:4)]);
    p=+det([mat1(:,1:2),mat1(:,4)]);
    q=-det(mat1(:,1:3));
    mat2=[[p1*p1';p2*p2';p3*p3'],mat1;2*q,[-m,-n,-p,0]];% size = 4x5
    A=+det(mat2(:,2:5));
    B=-det([mat2(:,1),mat2(:,3:5)]);
    C=+det([mat2(:,1:2),mat2(:,4:5)]);
    D=-det([mat2(:,1:3),mat2(:,5)]);
    E=+det(mat2(:,1:4));
    pc=-[B,C,D]/2/A;
    r=sqrt(B^2+C^2+D^2-4*A*E)/2/abs(A);
x1 =p1(1) ;y1 =p1(2) ;
x2 = pc(1)   ;y2 = pc(2);
x3 = p3(1);y3 =p3(2);
a2 = (x1-x2)*(x1-x2)+(y1-y2)*(y1-y2);
b2 = (x3-x2)*(x3-x2)+(y3-y2)*(y3-y2);
c2 = (x1-x3)*(x1-x3)+(y1-y3)*(y1-y3);
a = sqrt(a2);
b = sqrt(b2);
c = sqrt(c2);
pos = (a2+b2-c2)/(2*a*b);   
angle = acos(pos);       %圆心角 
realangle = angle*180/pi; %360角度zhi
	v1=realangle*2*(2000*r-84)*4/(1.8*64*2);%速度
	v2=realangle*2*(2000*r+84)*4/(1.8*64*2);
end
