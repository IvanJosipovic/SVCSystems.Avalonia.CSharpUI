using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public partial class GeneratedPropertyBindingTests : AvaloniaTestBase
{
    public sealed partial class GeneratedPropertyHost : Control, IDeclarativeViewBase
    {
        // Avalonia's property generator emits FlagProperty and the accessor bodies; CSharpUI's
        // generator must bind to that field instead of passing null to AvaloniaObject.Bind.
        [GeneratedDirectProperty]
        public partial bool Flag { get; set; }
    }

    private sealed class FlagSource : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _flag;

        public bool Flag
        {
            get => _flag;
            set
            {
                _flag = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Flag)));
            }
        }
    }

    [AvaloniaFact]
    public void Generated_direct_property_setter_binds_to_source_without_null_property()
    {
        var host = new GeneratedPropertyHost();
        var source = new FlagSource { Flag = true };

        host.Flag(source, x => x.Flag);

        Assert.True(host.Flag);

        source.Flag = false;
        Assert.False(host.Flag);
    }
}
