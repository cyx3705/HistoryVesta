% 验证z1计算公式的正确性
num_tests = 1000;  % 测试次数
tolerance = 1e-6;  % 容差

% 初始化错误标志
errors_found = false;

for i = 1:num_tests
    % 随机生成参数
    x = rand() * 20 - 10;  % -10到10之间的随机数
    y = rand() * 20 - 10;
    z = rand() * 20;      % 0到20之间的随机数（通常z为正）
    l1 = 10 + rand() * 6; % 10到16之间的随机数
    l2 = 8 + rand() * 4;  % 8到12之间的随机数
    l3 = 8 + rand() * 4;  % 8到12之间的随机数
    
    % 随机生成n（0到360度之间）
    n = rand() * 360;
    n_rad = n * pi/180;  % 将角度转换为弧度
    
    % 计算中间变量
    ls = l3*sin(n_rad);
    lc = l3*cos(n_rad);
    
    % 逆运动学计算
    try
        % 计算sita1、sita2、sita3
        l001 = acos(((z+ls).^2 + (sqrt(x^2+y^2)+lc).^2 - l1^2 - l2^2) / (2*l1*l2));
        l002 = asin(((z+ls).^2 + (sqrt(x^2+y^2)+lc).^2 + l2^2 - l1^2) / ...
                   (2*l2*sqrt((z+ls).^2 + (sqrt(x^2+y^2)+lc).^2)));
        
        % 统一使用角度单位（将弧度转换为角度）
        sita1 = rad2deg(atan(y/x)) * ones(size(n));
        sita2 = rad2deg(l001) + rad2deg(l002) - rad2deg(atan((sqrt(x^2+y^2)+lc)./(z+ls))-n_rad);
        sita3 = rad2deg(l002) - rad2deg(atan((sqrt(x^2+y^2)+lc)./(z+ls))-n_rad);
        sita4 = n;  % sita4直接等于n
        
        % 将角度转换为弧度
        sita2_rad = sita2 * pi/180;
        sita3_rad = sita3 * pi/180;
        sita4_rad = sita4 * pi/180;
        
        % 计算z1（根据正运动学）
        z1_calculated = l1*sin(sita2_rad)+l2*sin(sita3_rad)+l3*sin(sita4_rad);
        
        % 计算误差
        error = abs(z1_calculated - (z + ls));
        
        % 检查误差是否在容差范围内
        if error > tolerance
            fprintf('测试 %d 失败: 误差 = %.8f\n', i, error);
            fprintf('  参数: x=%.4f, y=%.4f, z=%.4f, l1=%.4f, l2=%.4f, l3=%.4f, n=%.4f\n', x, y, z, l1, l2, l3, n);
            fprintf('  计算值: z1_calculated=%.4f, 期望值=%.4f\n', z1_calculated, z + ls);
            errors_found = true;
        end
    catch e
        fprintf('测试 %d 出错: %s\n', i, e.message);
        errors_found = true;
    end
end

% 输出结果
if ~errors_found
    fprintf('所有测试通过！z1计算公式正确。\n');
else
    fprintf('发现错误！z1计算公式可能存在问题。\n');
end