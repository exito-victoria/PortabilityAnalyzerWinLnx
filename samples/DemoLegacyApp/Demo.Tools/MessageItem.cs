namespace Demo.Tools;

/// <summary>Usa BDUtils (que a su vez usa ViewModels de Windows). Windows por USO transitivo de segundo
/// nivel: MessageItem -> BDUtils -> TitleViewModel -> ViewModelBase -> DependencyObject.</summary>
public sealed class MessageItem
{
    public string Text { get; set; } = string.Empty;

    public string BuildTitledText()
    {
        var vm = BDUtils.BuildTitle(Text);
        return vm.Title;
    }
}
