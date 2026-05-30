% 定义参数x、y、z（可根据需要修改）
x = 4;
y = 5;
z = 7;
l1 = 13;
l2 = 10;
l3 = 10;

% 生成n的取值范围（0到90度，步长1度）
n = 0:360;
n_rad = n * pi/180;  % 将角度转换为弧度

ls = l3*sin(n_rad);
lc = l3*cos(n_rad);
r = sqrt(x^2+y^2);
% 定义sita1、sita2、sita3关于n、x、y、z的函数关系

% 数值稳定性处理
input_acos = ((z+ls).^2 + (r+lc).^2 - l1^2 - l2^2) / (2*l1*l2);
input_acos = min(max(input_acos, -0.999), 0.999);
l001 = acos(input_acos);

input_asin = ((z+ls).^2 + (r+lc).^2 + l2^2 - l1^2) / ...
            (2*l2*sqrt((z+ls).^2 + (r+lc).^2));
input_asin = min(max(input_asin, -0.999), 0.999);
l002 = asin(input_asin);

% 统一使用角度单位（将弧度转换为角度）
sita1 = rad2deg(atan2(y, x)) * ones(size(n));  % 使用atan2替代atan
sita2 = rad2deg(l001) + rad2deg(l002) - rad2deg(atan2((r+lc), (z+ls))-n_rad);
sita3 = rad2deg(l002) - rad2deg(atan2((r+lc), (z+ls))-n_rad);
sita4 = n;  % sita4直接等于n

% 修正z1计算：将角度转换为弧度
z1 = l1*sin(sita2*pi/180) + l2*sin(sita3*pi/180) + l3*sin(sita4*pi/180);

% 创建表格
T = table(n', sita1', sita2', sita3', sita4', z1', ...
    'VariableNames', {'n (度)', 'sita1', 'sita2', 'sita3', 'sita4', 'z1'});

% 显示表格
disp('生成的表格（展示前10行和后10行）:');
disp([T(1:10,:); T(end-9:end,:)]);

% 可选：导出Excel文件
writetable(T, 'sita_functions_table.xlsx');

% 绘制所有曲线在同一个图表中
figure;
plot(n, sita1, 'r-', n, sita2, 'g-', n, sita3, 'b-', n, sita4, 'm--', n, z1, 'k-', 'LineWidth', 1.5);
legend('sita1', 'sita2', 'sita3', 'sita4', 'z1');
xlabel('n (度)');
ylabel('函数值');
title('各角度随n的变化曲线');
grid on;

% 单独绘制z1曲线，便于观察
figure;
plot(n, z1, 'k-', 'LineWidth', 2);
xlabel('n (度)');
ylabel('z1值');
title('z1随n的变化曲线');
grid on;