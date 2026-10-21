using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace FindIt.App;

/// <summary>
/// Presents real controls to automation instead of generating data-item peers
/// whose names would be the presentation model's ToString() value.
/// </summary>
public sealed class PresentationItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PresentationContainerPeer(this);
}

/// <summary>
/// Keeps the virtualized ListBox layout while exposing the file/person buttons,
/// rather than the implementation's row objects, to assistive technology.
/// </summary>
public sealed class RowGallery : ListBox
{
    protected override DependencyObject GetContainerForItemOverride() => new GalleryRow();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is GalleryRow;
    protected override AutomationPeer OnCreateAutomationPeer() => new GalleryPeer(this);

    private sealed class GalleryRow : ListBoxItem
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new PresentationContainerPeer(this);
    }

    private sealed class GalleryPeer(RowGallery owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(RowGallery);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetNameCore()
        {
            var label = AutomationProperties.GetName(Owner);
            return string.IsNullOrWhiteSpace(label) ? "File gallery" : label;
        }
    }
}

/// <summary>Decorative icon-font glyphs do not need a separate spoken element.</summary>
public sealed class DecorativeTextBlock : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}

internal sealed class PresentationContainerPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    protected override string GetNameCore() => string.Empty;
    protected override bool IsControlElementCore() => false;
    protected override bool IsContentElementCore() => false;
}

