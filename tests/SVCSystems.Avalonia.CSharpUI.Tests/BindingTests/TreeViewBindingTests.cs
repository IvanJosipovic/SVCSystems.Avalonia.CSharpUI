using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public class TreeViewBindingTests : AvaloniaTestBase
{
    public class Node(string name, IEnumerable<Node>? children = null)
    {
        public string Name { get; set; } = name;

        public ObservableCollection<Node> Children { get; set; } = new(children ?? []);
    }

    public class TreeViewModel : INotifyPropertyChanged
    {
        private Node? _selectedNode;

        public Node? SelectedNode
        {
            get => _selectedNode;
            set
            {
                _selectedNode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedNodeName));
            }
        }

        public string SelectedNodeName => SelectedNode?.Name ?? "-";

        public ObservableCollection<Node> Nodes { get; set; } =
        [
            new Node("Animals", [new Node("Mammals", [new Node("Lion"), new Node("Cat"), new Node("Zebra")])])
        ];

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class TreeViewSampleView : ViewBase<TreeViewModel>
    {
        public TreeView Tree = null!;
        public TextBlock Text = null!;

        public TreeViewSampleView(TreeViewModel vm) : base(vm) { }

        protected override object Build(TreeViewModel vm) =>
            new StackPanel()
                .Children(
                    new TreeView()
                        .Ref(out Tree)
                        .ItemsSource(vm.Nodes)
                        .ItemTemplate(
                            new FuncTreeDataTemplate<Node>((n, _) => new TextBlock().Text(n.Name), n => n.Children)
                        )
                        .SelectedItem(vm, x => x.SelectedNode!, BindingMode.TwoWay),
                    new TextBlock().Ref(out Text).Text(vm, x => x.SelectedNodeName)
                );
    }

    [AvaloniaFact]
    public void TreeView_SelectedItem_TwoWay_Works()
    {
        var vm = new TreeViewModel();
        var view = new TreeViewSampleView(vm);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var lion = vm.Nodes[0].Children[0].Children[0];
        vm.SelectedNode = lion;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(lion, (Node?)view.Tree.SelectedItem);
        Assert.Equal("Lion", view.Text.Text);

        var cat = vm.Nodes[0].Children[0].Children[1];
        view.Tree.SelectedItem = cat;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(cat, vm.SelectedNode);
        Assert.Equal("Cat", view.Text.Text);
    }
}
