using BlueBubbles.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Controls;

public class ChatItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MessageTemplate { get; set; }
    public DataTemplate? DateSeparatorTemplate { get; set; }
    public DataTemplate? SystemEventTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        return item switch
        {
            MessageBubbleViewModel => MessageTemplate!,
            DateSeparatorViewModel => DateSeparatorTemplate!,
            SystemEventViewModel => SystemEventTemplate!,
            _ => MessageTemplate!
        };
    }
}
