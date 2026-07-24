//BaseVariable/ModuleInfoBase.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BaseVariable
{
    /// <summary>
    /// 所有模块信息基类
    /// </summary>
    public abstract class ModuleInfoBase
    {
        /// <summary>
        /// 模块名称（必须唯一，建议使用英文或驼峰）
        /// </summary>
        public abstract string ModuleName { get; }

        /// <summary>
        /// 模块描述
        /// </summary>
        public abstract string Description { get; }

        /// <summary>
        /// 作者
        /// </summary>
        public abstract string Author { get; }

        /// <summary>
        /// 版本号
        /// </summary>
        public abstract string Version { get; }

        /// <summary>
        /// 是否全暴露（如果为 true，则 MainClassType 可选，模块内所有公共类和方法都可被 DynamicController 调用；如果为 false，则只暴露 MainClassType 定义的核心业务类）
        /// </summary>
        public abstract bool Open { get; }

        /// <summary>
        /// 本模块要暴露的核心业务类（供 DynamicController 动态调用）
        /// 如果为 null，则不注册任何核心类
        /// </summary>
        public virtual Type? MainClassType => null;

#nullable enable
        /// <summary>
        /// 获取主类的单例实例。
        /// 如果 MainClassType 未设置、是静态类或创建失败，则返回 null。
        /// </summary>
        public virtual object? GetMainClassSingleton()
        {
            // MainClassType 是可选的，不设置就不创建实例
            if (MainClassType == null)
                return null;

            // 静态类（只有静态成员）无法实例化，返回 null
            if (MainClassType.IsAbstract && MainClassType.IsSealed)
                return null;

            try
            {
                // Activator.CreateInstance 可能返回 null（理论上极少），这里用 ?.
                return Activator.CreateInstance(MainClassType);
            }
            catch (Exception ex)
            {
                // 创建失败时记录日志（可选），但不抛异常，保持模板方法的宽松性
                Console.WriteLine($"[Warning] Failed to create instance of {MainClassType.FullName}: {ex.Message}");
                return null;
            }
        }
#nullable enable
        public virtual List<string> InitAddresses { get; } = new List<string>();

        /// <summary>
        /// 模块启动顺序（数字越小越早执行，默认为 100）
        /// 可用于控制模块初始化顺序（例如：基础模块先于业务模块）
        /// </summary>
        public virtual int InitializeOrder => 100;

        /// <summary>
        /// 是否启用该模块（默认启用，可用于配置开关）
        /// </summary>
        public virtual bool Enabled => true;
    }
}
