%单位：米
l=0.4;
a=0.4;
u=linspace(0,pi,10000);
rx=[];
ry=[];
theta=[]
for i=1:10000
    rx(end+1)=(-2*a*( ( l-cos(u(i)) ) * cos(u(i)) + (1-l) ));
  ry(end+1)=(2*a*(l-cos(u(i)))*sin(u(i)));
  %   rx(end+1)=0.3960.* cos(2.65.*(u(i) + 1.4));
  %  ry(end+1)=-0.99 .* sin(u(i) + 1.4);
end
plot(rx,ry)
hold on;


x_=inline('-2*0.4* ((0.4 - cos (t)) .*cos( t) + (1 - 0.4))','t');
y_=inline('2*0.4*(0.4 -cos (t)).* sin( t)','t');%内联函数
%x_=inline(' (99*cos((53*t)/20 + 371/100))/250','t');
%y_=inline('(99*sin(t + 7/5))/100','t');%内联函数

for i=0:15
scatter(x_(i*pi/15),y_(i*pi/15));
end;


x= -0.182384685;
y = 0.000286899;
r = 0.182384911;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal
hold on;

x = -0.195645925;
y = 0.009112672;
r = 0.198308001;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.209121207;
y = 0.035700379;
r = 0.228114823;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.208060663;
y = 0.075918341;
r = 0.268346759;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.185937452;
y = 0.117108676;
r = 0.315102007;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.145977911;
y = 0.146728049;
r = 0.364841532;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.097276607;
y = 0.157581751;
r = 0.414737026;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.050143624;
y = 0.148872991;
r = 0.462667221;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.012806273;
y = 0.124831585;
r = 0.50707461;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = 0.010248705;
y = 0.092464281;
r = 0.546812883;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = 0.018595255;
y = 0.059280449;
r = 0.581029739;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = 0.015222786;
y = 0.031424787;
r = 0.609088203;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = 0.005273482;
y = 0.012444595;
r = 0.630517284;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.005504472;
y = 0.002795708;
r = 0.644982299;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.012267678;
y = 0.0000821723457304602;
r = 0.652267683;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

x = -0.012267678;
y = -0.0000821723457300741;
r = 0.652267683;
rectangle('position',[x-r,y-r,2*r,2*r],'Curvature',[1,1],'EdgeColor','m')
axis equal

