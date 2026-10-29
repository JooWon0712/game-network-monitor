using System.Collections;
using System.Windows;
using System.Windows.Markup;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private void InstallPickerStyles()
        {
            ResourceDictionary styles = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='{x:Type ComboBox}'>
    <Setter Property='Background' Value='#13161A'/>
    <Setter Property='Foreground' Value='#E7EAEE'/>
    <Setter Property='BorderBrush' Value='#272C32'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='12,0,12,0'/>
    <Setter Property='MinHeight' Value='40'/>
    <Setter Property='MaxDropDownHeight' Value='300'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='FontFamily' Value='Consolas, Malgun Gothic'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Disabled'/>
    <Setter Property='ScrollViewer.VerticalScrollBarVisibility' Value='Auto'/>
    <Setter Property='ItemTemplate'>
      <Setter.Value><DataTemplate><TextBlock Text='{Binding}' TextTrimming='CharacterEllipsis'/></DataTemplate></Setter.Value>
    </Setter>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ComboBox}'>
          <Grid SnapsToDevicePixels='True'>
            <ToggleButton x:Name='PickerToggle' Focusable='False' ClickMode='Press'
                          IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'
                          Background='{TemplateBinding Background}' Foreground='{TemplateBinding Foreground}'
                          BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
                          Padding='{TemplateBinding Padding}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='{x:Type ToggleButton}'>
                  <Border x:Name='PickerShell' Background='{TemplateBinding Background}'
                          BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
                    <Grid Margin='{TemplateBinding Padding}'>
                      <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='24'/></Grid.ColumnDefinitions>
                      <ContentPresenter VerticalAlignment='Center' Margin='0,0,10,0' IsHitTestVisible='False'
                          Content='{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}'
                          ContentTemplate='{Binding SelectionBoxItemTemplate, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}'
                          ContentStringFormat='{Binding SelectionBoxItemStringFormat, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}'/>
                      <TextBlock x:Name='PickerArrow' Grid.Column='1' Text='[v]' Foreground='#939AA4' FontFamily='Consolas' FontSize='12'
                                 HorizontalAlignment='Center' VerticalAlignment='Center' IsHitTestVisible='False'/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                      <Setter TargetName='PickerShell' Property='Background' Value='#1B2026'/>
                      <Setter TargetName='PickerShell' Property='BorderBrush' Value='#57D6CE'/>
                    </Trigger>
                    <Trigger Property='IsChecked' Value='True'>
                      <Setter TargetName='PickerShell' Property='Background' Value='#1B2026'/>
                      <Setter TargetName='PickerShell' Property='BorderBrush' Value='#57D6CE'/>
                      <Setter TargetName='PickerArrow' Property='Foreground' Value='#57D6CE'/>
                      <Setter TargetName='PickerArrow' Property='Text' Value='[^]'/>
                    </Trigger>
                    <Trigger Property='IsEnabled' Value='False'>
                      <Setter TargetName='PickerShell' Property='Background' Value='#13161A'/>
                      <Setter TargetName='PickerShell' Property='BorderBrush' Value='#272C32'/>
                      <Setter TargetName='PickerArrow' Property='Foreground' Value='#69717C'/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom'
                   PlacementTarget='{Binding ElementName=PickerToggle}' VerticalOffset='2' Focusable='False'
                   AllowsTransparency='True' PopupAnimation='None'>
              <Border x:Name='PickerMenu' Background='#13161A' BorderBrush='#272C32' BorderThickness='1' Padding='2'
                      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
                      MaxHeight='{TemplateBinding MaxDropDownHeight}'>
                <ScrollViewer CanContentScroll='True' HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'>
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained' SnapsToDevicePixels='True'/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsKeyboardFocusWithin' Value='True'>
              <Setter TargetName='PickerToggle' Property='BorderBrush' Value='#57D6CE'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='{x:Type ComboBoxItem}'>
    <Setter Property='Foreground' Value='#E7EAEE'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='BorderBrush' Value='Transparent'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='10,8'/>
    <Setter Property='Margin' Value='1'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='FontFamily' Value='Consolas, Malgun Gothic'/>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='{x:Type ComboBoxItem}'>
        <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
        </Border>
      </ControlTemplate></Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='IsHighlighted' Value='True'>
        <Setter Property='Background' Value='#1B2026'/><Setter Property='BorderBrush' Value='#3B424C'/>
      </Trigger>
      <Trigger Property='IsSelected' Value='True'>
        <Setter Property='Background' Value='#20262D'/><Setter Property='BorderBrush' Value='#57D6CE'/>
        <Setter Property='Foreground' Value='#E7EAEE'/>
      </Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>");
            foreach (DictionaryEntry entry in styles) Resources.Add(entry.Key, entry.Value);
        }
    }
}
