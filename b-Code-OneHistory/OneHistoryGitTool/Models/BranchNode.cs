using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OneHistoryGitTool.Models;

/// <summary>
/// Git 操作继承树节点
/// 支持无限层级嵌套
/// </summary>
public class BranchNode : INotifyPropertyChanged
{
    private string _branchName = string.Empty;
    private string _description = string.Empty;
    private string _lastPushTime = string.Empty;
    private bool _isExpanded = true;

    public string BranchName
    {
        get => _branchName;
        set => SetProperty(ref _branchName, value);
    }

    /// <summary>
    /// 推送描述（通常是最后一次 commit 的 message）
    /// </summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    /// <summary>
    /// 最后一次推送/提交的时间
    /// </summary>
    public string LastPushTime
    {
        get => _lastPushTime;
        set => SetProperty(ref _lastPushTime, value);
    }

    /// <summary>
    /// 子节点（支持无限层级）
    /// </summary>
    public ObservableCollection<BranchNode> Children { get; } = new();

    /// <summary>
    /// 是否展开（默认展开根节点和一级）
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// 完整显示文本（用于搜索或工具提示）
    /// </summary>
    public string FullDisplay => $"{BranchName} - {Description}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
