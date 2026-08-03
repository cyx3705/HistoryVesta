using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal sealed class SolidWorksInteropBridge : IDisposable
{
    private readonly object _application;
    private readonly Type _applicationInterface;
    private readonly Type _modelInterface;
    private readonly Type _extensionInterface;
    private readonly Type _frameInterface;
    private readonly Type _featureInterface;
    private readonly Type _sketchInterface;
    private readonly Type _sketchManagerInterface;
    private readonly Type _partInterface;
    private readonly Type _bodyInterface;
    private readonly Type _entityInterface;
    private readonly Type _assemblyInterface;
    private readonly Type _componentInterface;
    private readonly Type _mathUtilityInterface;
    private readonly Type _mathTransformInterface;
    private readonly string _installDirectory;
    private Type? _featureWorksInterface;

    private SolidWorksInteropBridge(object application, Assembly interopAssembly, string installDirectory)
    {
        _installDirectory = installDirectory;
        _applicationInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISldWorks");
        _modelInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IModelDoc2");
        _extensionInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IModelDocExtension");
        _frameInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IFrame");
        _featureInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IFeature");
        _sketchInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISketch");
        _sketchManagerInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISketchManager");
        _partInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IPartDoc");
        _bodyInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IBody2");
        _entityInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IEntity");
        _assemblyInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IAssemblyDoc");
        _componentInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IComponent2");
        _mathUtilityInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IMathUtility");
        _mathTransformInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IMathTransform");

        var unknown = Marshal.GetIUnknownForObject(application);
        try
        {
            _application = Marshal.GetTypedObjectForIUnknown(unknown, _applicationInterface);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public static SolidWorksInteropBridge Create(object application, Type applicationComType)
    {
        var installDirectory = ResolveInstallDirectory(applicationComType.GUID);
        var interopPath = Path.Combine(installDirectory, "api", "redist", "SolidWorks.Interop.sldworks.dll");
        if (!File.Exists(interopPath))
        {
            throw new FileNotFoundException(
                "未在 SolidWorks 安装目录找到官方 Interop 程序集。",
                interopPath);
        }
        return new SolidWorksInteropBridge(application, Assembly.LoadFrom(interopPath), installDirectory);
    }

    public long GetWindowHandle()
    {
        object? frame = null;
        try
        {
            frame = Invoke(_applicationInterface, _application, "IFrameObject");
            return frame is null
                ? 0
                : Convert.ToInt64(Invoke(_frameInterface, frame, "GetHWndx64"));
        }
        finally
        {
            ComRelease.Final(frame);
        }
    }

    public object? GetImportFileData(string path)
        => Invoke(_applicationInterface, _application, "GetImportFileData", path);

    public object? LoadFile4(string path, string arguments, object? importData, out int errors)
    {
        object?[] parameters = [path, arguments, importData, 0];
        var model = Invoke(_applicationInterface, _application, "LoadFile4", parameters);
        errors = Convert.ToInt32(parameters[3]);
        return model;
    }

    public int GetDocumentType(object model)
        => Convert.ToInt32(Invoke(_modelInterface, model, "GetType"));

    public string GetTitle(object model)
        => Convert.ToString(Invoke(_modelInterface, model, "GetTitle")) ?? string.Empty;

    public object GetExtension(object model)
        => Invoke(_modelInterface, model, "get_Extension")
            ?? throw new InvalidOperationException("SolidWorks 未返回 ModelDocExtension。");

    public bool SaveAs3(
        object extension,
        string path,
        int version,
        int options,
        out int errors,
        out int warnings)
    {
        object?[] parameters = [path, version, options, null, null, 0, 0];
        var saved = Convert.ToBoolean(Invoke(_extensionInterface, extension, "SaveAs3", parameters));
        errors = Convert.ToInt32(parameters[5]);
        warnings = Convert.ToInt32(parameters[6]);
        return saved;
    }

    public void CloseDocument(string title)
        => Invoke(_applicationInterface, _application, "CloseDoc", title);

    public void ExitApplication()
        => Invoke(_applicationInterface, _application, "ExitApp");

    // ---------------- V3.0：装配体创建 ----------------

    public string GetAssemblyTemplate()
    {
        const int defaultAssemblyTemplate = 9;
        var template = Convert.ToString(Invoke(
            _applicationInterface,
            _application,
            "GetUserPreferenceStringValue",
            defaultAssemblyTemplate)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;
        var resolved = Convert.ToString(Invoke(
            _applicationInterface,
            _application,
            "GetDocumentTemplate",
            2, template, 0, 0d, 0d));
        return string.IsNullOrWhiteSpace(resolved) ? template : resolved;
    }

    public object? NewAssembly(string template)
        => Invoke(_applicationInterface, _application, "NewDocument", template, 0, 0d, 0d);

    public string InstallDirectory => _installDirectory;

    // swDocumentTypes_e
    private const int DocumentTypePart = 1;
    private const int DocumentTypeAssembly = 2;

    public object? OpenPart(string path, out int errors, out int warnings)
        => OpenDocument(path, DocumentTypePart, out errors, out warnings);

    /// <summary>V3.3：嵌套装配要把子装配 .SLDASM 也打开，文档类型不能再写死为零件。</summary>
    public object? OpenComponentDocument(string path, out int errors, out int warnings)
        => OpenDocument(
            path,
            ConversionPathLayout.HasExtension(path, ConversionArtifactKind.SolidWorksAssembly)
                ? DocumentTypeAssembly
                : DocumentTypePart,
            out errors,
            out warnings);

    private object? OpenDocument(string path, int documentType, out int errors, out int warnings)
    {
        object?[] parameters = [path, documentType, 1, string.Empty, 0, 0];
        var model = Invoke(_applicationInterface, _application, "OpenDoc6", parameters);
        errors = Convert.ToInt32(parameters[4]);
        warnings = Convert.ToInt32(parameters[5]);
        return model;
    }

    public object GetMathUtility()
        => Invoke(_applicationInterface, _application, "GetMathUtility")
            ?? throw new InvalidOperationException("SolidWorks 未返回 MathUtility。");

    public object? AddComponent(object assembly, string partPath)
        => Invoke(_assemblyInterface, assembly, "AddComponent5", partPath, 0, string.Empty, false, string.Empty, 0d, 0d, 0d);

    public object CreateTransform(object mathUtility, double[] values)
        => Invoke(_mathUtilityInterface, mathUtility, "CreateTransform", values)
            ?? throw new InvalidOperationException("SolidWorks CreateTransform 返回 null。");

    public void SetComponentTransform(object component, object transform)
        => Invoke(_componentInterface, component, "set_Transform2", transform);

    public double[] GetComponentTransform(object component)
    {
        object? transform = null;
        try
        {
            transform = Invoke(_componentInterface, component, "get_Transform2")
                ?? throw new InvalidOperationException("SolidWorks 组件没有 Transform2。");
            var raw = Invoke(_mathTransformInterface, transform, "get_ArrayData") as Array
                ?? throw new InvalidDataException("SolidWorks MathTransform.ArrayData 无效。");
            return raw.Cast<object>().Select(Convert.ToDouble).ToArray();
        }
        finally
        {
            ComRelease.Final(transform);
        }
    }

    public bool SelectComponent(object component, bool append)
        => Convert.ToBoolean(Invoke(_componentInterface, component, "Select4", append, null, false));

    public bool IsComponentFixed(object component)
        => Convert.ToBoolean(Invoke(_componentInterface, component, "IsFixed"));

    public void FixSelectedComponents(object assembly)
        => Invoke(_assemblyInterface, assembly, "FixComponent");

    // ---------------- V2.0：特征识别与草图完全定义 ----------------

    public bool GetUserPreferenceToggle(int preference)
        => Convert.ToBoolean(Invoke(_applicationInterface, _application, "GetUserPreferenceToggle", preference));

    public void SetUserPreferenceToggle(int preference, bool value)
        => Invoke(_applicationInterface, _application, "SetUserPreferenceToggle", preference, value);

    /// <summary>FeatureWorks 只作用于活动文档；附着到用户已有会话时导入的文档不一定是活动的。</summary>
    public int ActivateDocument(string title)
    {
        object?[] parameters = [title, false, 0, 0];
        object? activated = null;
        try
        {
            activated = Invoke(_applicationInterface, _application, "ActivateDoc3", parameters);
            return Convert.ToInt32(parameters[3]);
        }
        finally
        {
            // ActivateDoc3 can return the same RCW held by the import loop. Release one COM
            // reference only; FinalReleaseComObject would invalidate the caller's model RCW.
            ComRelease.One(activated);
        }
    }

    public string GetActiveDocumentTitle()
    {
        object? active = null;
        try
        {
            active = Invoke(_applicationInterface, _application, "get_ActiveDoc");
            return active is null ? "(无活动文档)" : GetTitle(active);
        }
        catch (Exception ex)
        {
            return "(读取失败:" + ex.Message + ")";
        }
        finally
        {
            ComRelease.One(active);
        }
    }

    public object? GetAddInObject(string progId)
        => Invoke(_applicationInterface, _application, "GetAddInObject", progId);

    public int LoadAddIn(string path)
        => Convert.ToInt32(Invoke(_applicationInterface, _application, "LoadAddIn", path));

    /// <summary>
    /// FeatureWorks 的 InprocServer32 是相对路径 ".\fworks\fworks.dll"，
    /// 必须拼上 SolidWorks 安装目录。CLSID 来自 FeatureWorks.FeatureWorksApp 的注册。
    /// </summary>
    public string? ResolveFeatureWorksPath()
    {
        const string featureWorksClassId = "{7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}";
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{featureWorksClassId}\InprocServer32");
        if (key?.GetValue(null) is not string registered || string.IsNullOrWhiteSpace(registered))
            return null;
        var server = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
        return Path.IsPathFullyQualified(server)
            ? server
            : Path.GetFullPath(Path.Combine(_installDirectory, server));
    }

    public int RecognizeFeatureAutomatic(object featureWorks, int options)
        => Convert.ToInt32(Invoke(FeatureWorksInterface(), featureWorks, "RecognizeFeatureAutomatic", options));

    public bool CreateFeatures(object featureWorks, short options)
        => Convert.ToBoolean(Invoke(FeatureWorksInterface(), featureWorks, "CreateFeatures", options));

    public bool SetAdvancedOptions(object featureWorks, short options)
        => Convert.ToBoolean(Invoke(FeatureWorksInterface(), featureWorks, "SetAdvancedOptions", options));

    public bool SetPerformanceOptions(object featureWorks, short options)
        => Convert.ToBoolean(Invoke(FeatureWorksInterface(), featureWorks, "SetPerformanceOptions", options));

    public void ClearSelection(object model)
        => Invoke(_modelInterface, model, "ClearSelection2", true);

    /// <summary>
    /// 自动识别必须先预选一个种子面：不预选时 RecognizeFeatureAutomatic 恒返回 0 且不报错。
    /// </summary>
    /// <returns>保持实体和面 RCW 存活的选择租约；调用识别方法后再释放。</returns>
    public SeedFaceSelection SelectSeedFace(object model)
    {
        object? body = null;
        object? face = null;
        try
        {
            var raw = Invoke(_partInterface, model, "GetBodies2", 0 /* swSolidBody */, false);
            if (raw is null)
                return SeedFaceSelection.Failed("GetBodies2 返回 null");
            if (raw is not Array bodies)
                return SeedFaceSelection.Failed($"GetBodies2 返回 {raw.GetType().FullName}，不是数组");
            if (bodies.Length == 0)
                return SeedFaceSelection.Failed("零件中没有实体");

            body = bodies.GetValue(0);
            if (body is null)
                return SeedFaceSelection.Failed("首个实体为空");
            face = Invoke(_bodyInterface, body, "GetFirstFace");
            if (face is null)
                return SeedFaceSelection.Failed("GetFirstFace 返回 null");

            if (!Convert.ToBoolean(Invoke(_entityInterface, face, "Select4", false, null)))
                return SeedFaceSelection.Failed("Entity.Select4 返回 false");

            var selection = SeedFaceSelection.Selected(body, face);
            body = null;
            face = null;
            return selection;
        }
        finally
        {
            ComRelease.Final(face);
            ComRelease.Final(body);
        }
    }

    public object? FirstFeature(object model)
        => Invoke(_modelInterface, model, "FirstFeature");

    public object? NextFeature(object feature)
        => Invoke(_featureInterface, feature, "GetNextFeature");

    public string FeatureTypeName(object feature)
        => Convert.ToString(Invoke(_featureInterface, feature, "GetTypeName2")) ?? string.Empty;

    public string FeatureName(object feature)
        => Convert.ToString(Invoke(_featureInterface, feature, "get_Name")) ?? string.Empty;

    public bool SelectFeature(object feature)
        => Convert.ToBoolean(Invoke(_featureInterface, feature, "Select2", false, 0));

    public object? SpecificFeature(object feature)
        => Invoke(_featureInterface, feature, "GetSpecificFeature2");

    public object GetSketchManager(object model)
        => Invoke(_modelInterface, model, "get_SketchManager")
            ?? throw new InvalidOperationException("SolidWorks 未返回 SketchManager。");

    public void InsertSketch(object sketchManager, bool updateEditRebuild)
        => Invoke(_sketchManagerInterface, sketchManager, "InsertSketch", updateEditRebuild);

    public bool SelectByID2(object extension, string name, string type, int mark)
        => Convert.ToBoolean(Invoke(
            _extensionInterface,
            extension,
            "SelectByID2",
            name, type, 0d, 0d, 0d, false, mark, null, 0));

    /// <summary>返回值不可用：SDK 明确写 "Not currently defined"，必须靠 GetConstrainedStatus 验收。</summary>
    public void FullyDefineSketch(object sketchManager, int relations)
        => Invoke(
            _sketchManagerInterface,
            sketchManager,
            "FullyDefineSketch",
            true, true, relations, true, 1, null, 1, null, 1, 1);

    public int GetConstrainedStatus(object sketch)
        => Convert.ToInt32(Invoke(_sketchInterface, sketch, "GetConstrainedStatus"));

    private Type FeatureWorksInterface()
    {
        if (_featureWorksInterface is not null)
            return _featureWorksInterface;

        var path = Path.Combine(_installDirectory, "api", "redist", "SolidWorks.Interop.fworks.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "未在 SolidWorks 安装目录找到 FeatureWorks 官方 Interop 程序集。",
                path);
        }

        _featureWorksInterface = GetType(
            Assembly.LoadFrom(path),
            "SolidWorks.Interop.fworks.IFeatureWorksApp");
        return _featureWorksInterface;
    }

    public void Dispose()
        => ComRelease.Final(_application);

    private static object? Invoke(Type interfaceType, object target, string methodName, params object?[]? parameters)
    {
        try
        {
            var method = interfaceType.GetMethod(methodName)
                ?? throw new MissingMethodException(interfaceType.FullName, methodName);
            return method.Invoke(target, parameters);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static Type GetType(Assembly assembly, string name)
        => assembly.GetType(name, throwOnError: true)
            ?? throw new TypeLoadException($"SolidWorks Interop 缺少类型：{name}");

    private static string ResolveInstallDirectory(Guid applicationClassId)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{applicationClassId}}}\LocalServer32");
        var registeredServer = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(registeredServer))
            throw new InvalidOperationException("无法从 COM 注册解析 SolidWorks 安装路径。");

        var executablePath = Environment.ExpandEnvironmentVariables(registeredServer.Trim().Trim('"'));
        return Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("SolidWorks COM 注册路径无效。");
    }
}

internal sealed class SeedFaceSelection : IDisposable
{
    private object? _body;
    private object? _face;

    private SeedFaceSelection(object? body, object? face, string? failure)
    {
        _body = body;
        _face = face;
        Failure = failure;
    }

    public string? Failure { get; }

    public static SeedFaceSelection Selected(object body, object face)
        => new(body, face, null);

    public static SeedFaceSelection Failed(string failure)
        => new(null, null, failure);

    public void Dispose()
    {
        ComRelease.Final(_face);
        ComRelease.Final(_body);
        _face = null;
        _body = null;
    }
}
