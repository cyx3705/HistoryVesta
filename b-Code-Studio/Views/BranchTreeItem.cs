using System.ComponentModel;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Views;

internal sealed class BranchTreeItem : INotifyPropertyChanged
{
    private bool _isExpanded;

    private BranchTreeItem(BranchTreeNode node)
    {
        BranchName = node.BranchName;
        Description = node.Description;
        LastPushTime = node.LastPushTime;
        Children = node.Children.Select(FromContract).ToList();
    }

    public string BranchName { get; }

    public string Description { get; }

    public string LastPushTime { get; }

    public IReadOnlyList<BranchTreeItem> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static BranchTreeItem FromContract(BranchTreeNode node) => new(node);
}
