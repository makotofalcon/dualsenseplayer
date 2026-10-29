[CmdletBinding()]
param([string]$SourceRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

# Load the actual controls without starting the player, devices, or saved settings.
[xml]$applicationMarkup = [IO.File]::ReadAllText((Join-Path $SourceRoot 'src/DualSensePlayer.App/App.xaml'), [Text.Encoding]::UTF8)
$applicationMarkup.DocumentElement.RemoveAttribute('Class', 'http://schemas.microsoft.com/winfx/2006/xaml')
$application = [Windows.Markup.XamlReader]::Parse($applicationMarkup.OuterXml)
[xml]$markup = [IO.File]::ReadAllText((Join-Path $SourceRoot 'src/DualSensePlayer.App/MainWindow.xaml'), [Text.Encoding]::UTF8)
foreach ($element in $markup.SelectNodes('//*')) {
    foreach ($attribute in @($element.Attributes)) {
        if (($attribute.LocalName -eq 'Class' -and $attribute.NamespaceURI -eq 'http://schemas.microsoft.com/winfx/2006/xaml') -or
            $attribute.Value -cmatch '^On[A-Z]') {
            $element.RemoveAttributeNode($attribute) | Out-Null
        }
    }
}
$window = [Windows.Markup.XamlReader]::Parse($markup.OuterXml)
try {
    foreach ($name in @('Volume', 'Gain1', 'Gain2')) {
        $slider = $window.FindName($name)
        foreach ($percent in @(0, 25, 50, 100)) {
            $slider.Value = $percent
            if ($slider.Value -ne $percent) { throw "$name clipped $percent percent to $($slider.Value)." }
        }
    }
    $content = $window.Content
    $content.Measure([Windows.Size]::new(1000, 720))
    $content.Arrange([Windows.Rect]::new(0, 0, 1000, 720))
    $content.UpdateLayout()
    $scroll = $content.Children | Where-Object { $_ -is [Windows.Controls.ScrollViewer] } | Select-Object -First 1
    if (-not $scroll -or $scroll.ViewportHeight -le 0) { throw 'The management scroll viewport has no height.' }
    foreach ($name in @('Controller1', 'Controller2')) {
        $control = $window.FindName($name)
        $bounds = $control.TransformToAncestor($scroll).TransformBounds([Windows.Rect]::new(0, 0, $control.ActualWidth, $control.ActualHeight))
        if ($control.ActualHeight -le 0 -or $bounds.Top -lt 0 -or $bounds.Bottom -gt $scroll.ViewportHeight) {
            throw "$name is not visible without scrolling: $bounds, viewport $($scroll.ViewportHeight)."
        }
    }
    Write-Host 'PASS: gain controls retain 0/25/50/100 percent; both controller selectors are visible without scrolling.'
} finally { $window.Close(); $application.Shutdown() }
