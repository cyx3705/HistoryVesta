% 定义参数x、y、z（可根据需要修改）
x = 3;
y = 4;
z = 8;
l1 = 13;
l2 = 10;
l3 = 10;

% 生成n的取值范围（0到360度，步长1度）
n =0:180;
n_rad = n * pi/180;  % 将角度转换为弧度

ls = l3*sin(n_rad);
lc = l3*cos(n_rad);

% 定义sita1、sita2、sita3关于n、x、y、z的函数关系
l001 = acos(((z+ls).^2 + (sqrt(x^2+y^2)+lc).^2 - l1^2 - l2^2) / (2*l1*l2));
l002 = asin(((z+ls).^2 + (sqrt(x^2+y^2)+lc).^2 + l2^2 - l1^2) / ...
           (2*l2*sqrt((z+ls).^2 + (sqrt(x^2+y^2)+lc).^2)));

% 统一使用角度单位（将弧度转换为角度）
sita1 = rad2deg(atan(y/x)) * ones(size(n));
na = rad2deg(l001) + rad2deg(l002) - rad2deg(atan((sqrt(x^2+y^2)+lc)./(z+ls))-n_rad);
sita3 = rad2deg(l002) - rad2deg(atan((sqrt(x^2+y^2)+lc)./(z+ls))-n_rad);
sita4 = n;  % sita4直接等于n

z1 = l1*sin(sita2)+l2*sin(sita3)+l3*sin(sita4); 

% 创建表格 - 修正了变量名称的引号问题
T = table(n', sita1', sita2', sita3', sita4', 'VariableNames', {'n (度)', 'sita1', 'sita2', 'sita3', 'sita4'});

% 显示表格
disp('生成的表格（展示前10行和后10行）:');
disp([T(1:10,:); T(end-9:end,:)]);

% 可选：导出Excel文件
writetable(T, 'sita_functions_table.xlsx');

% 绘制所有曲线在同一个图表中
figure;
plot(n, sita1, 'r-', n, sita2, 'g-', n, sita3, 'b-', n, sita4, 'm--', n, z1, 'm-', 'LineWidth', 1.5);
legend('sita1', 'sita2', 'sita3', 'sita4', 'z1'); % 修正了图例
xlabel('n (度)');
ylabel('函数值（度）');  % 更新y轴标签为度
title('sita1、sita2、sita3、sita4随n的变化曲线');
grid on;