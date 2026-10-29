using System.Collections;
using System.Windows;
using System.Windows.Markup;

namespace PcInternetMonitor
{
    public sealed partial class MainWindow
    {
        private void InstallTerminalStyles()
        {
            ResourceDictionary styles = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='{x:Type ToolTip}'>
    <Setter Property='Background' Value='#13161A'/>
    <Setter Property='Foreground' Value='#E7EAEE'/>
    <Setter Property='BorderBrush' Value='#272C32'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='10,8'/>
    <Setter Property='FontFamily' Value='Consolas, Malgun Gothic'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='MaxWidth' Value='460'/>
    <Setter Property='TextBlock.TextWrapping' Value='Wrap'/>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='{x:Type ToolTip}'>
        <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>
          <ContentPresenter/>
        </Border>
      </ControlTemplate></Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='{x:Type Button}'>
    <Setter Property='Background' Value='#13161A'/>
    <Setter Property='Foreground' Value='#E7EAEE'/>
    <Setter Property='BorderBrush' Value='#272C32'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='14,8'/>
    <Setter Property='FontFamily' Value='Consolas, Malgun Gothic'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='{x:Type Button}'>
        <Border x:Name='ButtonShell' Background='{TemplateBinding Background}'
                BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
                Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='ButtonShell' Property='BorderBrush' Value='#57D6CE'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'>
            <Setter TargetName='ButtonShell' Property='BorderBrush' Value='#57D6CE'/>
          </Trigger>
          <Trigger Property='IsPressed' Value='True'>
            <Setter TargetName='ButtonShell' Property='Opacity' Value='0.72'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter TargetName='ButtonShell' Property='Opacity' Value='0.55'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='{x:Type TextBox}'>
    <Setter Property='Background' Value='#0D0F12'/>
    <Setter Property='Foreground' Value='#E7EAEE'/>
    <Setter Property='BorderBrush' Value='#272C32'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='9,6'/>
    <Setter Property='FontFamily' Value='Consolas, Malgun Gothic'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='CaretBrush' Value='#57D6CE'/>
    <Setter Property='SelectionBrush' Value='#3A414B'/>
    <Style.Triggers>
      <Trigger Property='IsKeyboardFocusWithin' Value='True'>
        <Setter Property='BorderBrush' Value='#57D6CE'/>
      </Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='{x:Type TabItem}'>
    <Setter Property='Foreground' Value='#939AA4'/>
    <Setter Property='Background' Value='#0D0F12'/>
    <Setter Property='BorderBrush' Value='Transparent'/>
    <Setter Property='BorderThickness' Value='0,0,0,1'/>
    <Setter Property='Padding' Value='16,9'/>
    <Setter Property='FontFamily' Value='Consolas, Malgun Gothic'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='{x:Type TabItem}'>
        <Border x:Name='TabShell' Background='{TemplateBinding Background}'
                BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
                Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
          <ContentPresenter ContentSource='Header' RecognizesAccessKey='True'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsSelected' Value='True'>
            <Setter Property='Foreground' Value='#57D6CE'/>
            <Setter TargetName='TabShell' Property='Background' Value='#13161A'/>
            <Setter TargetName='TabShell' Property='BorderBrush' Value='#57D6CE'/>
            <Setter TargetName='TabShell' Property='BorderThickness' Value='0,0,0,2'/>
          </Trigger>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='TabShell' Property='BorderBrush' Value='#57D6CE'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'>
            <Setter TargetName='TabShell' Property='BorderBrush' Value='#57D6CE'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='{x:Type Expander}'>
    <Setter Property='Foreground' Value='#939AA4'/>
    <Setter Property='BorderBrush' Value='#272C32'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='FontFamily' Value='Consolas, Malgun Gothic'/>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='{x:Type Expander}'>
        <DockPanel LastChildFill='True' Background='{TemplateBinding Background}'>
          <ToggleButton x:Name='HeaderSite' DockPanel.Dock='Top' Focusable='True'
                        Foreground='{TemplateBinding Foreground}' Background='Transparent'
                        BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='0,0,0,1'
                        HorizontalContentAlignment='Stretch' FontFamily='{TemplateBinding FontFamily}'
                        Content='{TemplateBinding Header}' ContentTemplate='{TemplateBinding HeaderTemplate}'
                        ContentTemplateSelector='{TemplateBinding HeaderTemplateSelector}'
                        ContentStringFormat='{TemplateBinding HeaderStringFormat}'
                        IsChecked='{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
            <ToggleButton.Template>
              <ControlTemplate TargetType='{x:Type ToggleButton}'>
                <Border x:Name='HeaderShell' Background='{TemplateBinding Background}'
                        BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
                        Padding='0,5,0,6' SnapsToDevicePixels='True'>
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width='30'/><ColumnDefinition/></Grid.ColumnDefinitions>
                    <TextBlock x:Name='DisclosureMark' Text='[+]' FontFamily='Consolas' FontSize='12'
                               Foreground='#939AA4' VerticalAlignment='Center'/>
                    <ContentPresenter Grid.Column='1' HorizontalAlignment='Left' VerticalAlignment='Center'
                                      RecognizesAccessKey='True'/>
                  </Grid>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsChecked' Value='True'>
                    <Setter TargetName='DisclosureMark' Property='Text' Value='[-]'/>
                    <Setter TargetName='DisclosureMark' Property='Foreground' Value='#57D6CE'/>
                  </Trigger>
                  <Trigger Property='IsMouseOver' Value='True'>
                    <Setter TargetName='HeaderShell' Property='BorderBrush' Value='#57D6CE'/>
                    <Setter TargetName='DisclosureMark' Property='Foreground' Value='#57D6CE'/>
                  </Trigger>
                  <Trigger Property='IsKeyboardFocused' Value='True'>
                    <Setter TargetName='HeaderShell' Property='BorderBrush' Value='#57D6CE'/>
                    <Setter TargetName='DisclosureMark' Property='Foreground' Value='#57D6CE'/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </ToggleButton.Template>
          </ToggleButton>
          <ContentPresenter x:Name='ExpandSite' ContentSource='Content' Visibility='Collapsed'
                            HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                            VerticalAlignment='{TemplateBinding VerticalContentAlignment}'
                            Margin='{TemplateBinding Padding}'/>
        </DockPanel>
        <ControlTemplate.Triggers>
          <Trigger Property='IsExpanded' Value='True'>
            <Setter TargetName='ExpandSite' Property='Visibility' Value='Visible'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter TargetName='HeaderSite' Property='Opacity' Value='0.55'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='TerminalScrollThumb' TargetType='{x:Type Thumb}'>
    <Setter Property='Background' Value='#4D5561'/>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='{x:Type Thumb}'>
        <Border x:Name='ThumbShell' Background='{TemplateBinding Background}' Margin='3' MinHeight='6' MinWidth='6'/>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ThumbShell' Property='Background' Value='#57D6CE'/></Trigger>
          <Trigger Property='IsDragging' Value='True'><Setter TargetName='ThumbShell' Property='Background' Value='#57D6CE'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='TerminalScrollPage' TargetType='{x:Type RepeatButton}'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='{x:Type ScrollBar}'>
    <Setter Property='Width' Value='14'/>
    <Setter Property='Background' Value='#0D0F12'/>
    <Setter Property='BorderBrush' Value='#272C32'/>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'>
        <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1,0,0,0'>
          <Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}'
                 Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{TemplateBinding Value}'
                 ViewportSize='{TemplateBinding ViewportSize}'>
            <Track.DecreaseRepeatButton><RepeatButton x:Name='PageDecrease' Command='{x:Static ScrollBar.PageUpCommand}' Style='{StaticResource TerminalScrollPage}'/></Track.DecreaseRepeatButton>
            <Track.Thumb><Thumb Style='{StaticResource TerminalScrollThumb}'/></Track.Thumb>
            <Track.IncreaseRepeatButton><RepeatButton x:Name='PageIncrease' Command='{x:Static ScrollBar.PageDownCommand}' Style='{StaticResource TerminalScrollPage}'/></Track.IncreaseRepeatButton>
          </Track>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='Orientation' Value='Horizontal'>
            <Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>
            <Setter TargetName='PageDecrease' Property='Command' Value='{x:Static ScrollBar.PageLeftCommand}'/>
            <Setter TargetName='PageIncrease' Property='Command' Value='{x:Static ScrollBar.PageRightCommand}'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='14'/></Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>");
            foreach (DictionaryEntry entry in styles) Resources.Add(entry.Key, entry.Value);
        }
    }
}
