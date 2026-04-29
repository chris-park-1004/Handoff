$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
$preview = [Console]::In.ReadToEnd()

Add-Type -AssemblyName PresentationFramework

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="team-context: injection request"
        Width="640" Height="420"
        WindowStartupLocation="CenterScreen"
        Topmost="True">
  <DockPanel Margin="12">
    <TextBlock DockPanel.Dock="Top"
               Text="About to inject the following context into Claude Code:"
               FontWeight="Bold"
               Margin="0,0,0,8"/>
    <StackPanel DockPanel.Dock="Bottom"
                Orientation="Horizontal"
                HorizontalAlignment="Right"
                Margin="0,12,0,0">
      <Button x:Name="BtnAllow" Content="Allow" Width="90" Height="28" Margin="0,0,8,0"/>
      <Button x:Name="BtnDeny"  Content="Deny"  Width="90" Height="28" IsCancel="True"/>
    </StackPanel>
    <Border BorderBrush="#CCC" BorderThickness="1">
      <ScrollViewer VerticalScrollBarVisibility="Auto">
        <TextBox x:Name="TxtPreview"
                 IsReadOnly="True"
                 TextWrapping="Wrap"
                 AcceptsReturn="True"
                 BorderThickness="0"
                 FontFamily="Consolas"
                 Padding="8"/>
      </ScrollViewer>
    </Border>
  </DockPanel>
</Window>
"@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)
$window.FindName('TxtPreview').Text = $preview

$script:choice = 1
$window.FindName('BtnAllow').Add_Click({
  $script:choice = 0
  $window.Close()
})
$window.FindName('BtnDeny').Add_Click({
  $script:choice = 1
  $window.Close()
})

[void]$window.ShowDialog()
exit $script:choice
