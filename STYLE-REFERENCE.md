# KillerScan Style Reference
## Derived from KillerTools (killertools.net)

## Color Palette

### Primary (Green)
- Primary:        #1ea54c
- Primary Hover:  #36AD6A
- Primary Pressed: #0C7A43
- Primary Suppl:  #36AD6A

### Backgrounds (Dark Mode)
- App Background: #1c1c1c
- Surface/Cards:  #232323
- Card Border:    #282828
- Table Header:   #353535
- Table Cells:    #232323
- Light BG:       #f1f5f9  (if light mode ever needed)

### Scrollbars
- Thumb:          #1ea54c55  (primary at ~33% opacity)
- Thumb Hover:    #1ea54c
- Track:          transparent
- Width:          4px

### Notifications
- Background:     #333333

## Typography
- Font: System font stack (inherit)
- Weight: 400 (normal) for body
- Buttons: ~14px, 400 weight

## UI Patterns
- Dark-first design
- 4px border-radius on buttons (not fully round)
- Cards as primary content containers
- Minimal chrome, clean layout
- Thin scrollbars in accent green

## Icons
- Source: Tabler Icons (MIT licensed)
  - Website uses @tabler/icons via unplugin-icons
  - For WPF: use Tabler SVG icons directly
  - Download: https://tabler.io/icons
  - GitHub: https://github.com/tabler/tabler-icons

## WPF Implementation Notes

### ResourceDictionary Colors
```xml
<Color x:Key="PrimaryColor">#1ea54c</Color>
<Color x:Key="PrimaryHoverColor">#36AD6A</Color>
<Color x:Key="PrimaryPressedColor">#0C7A43</Color>
<Color x:Key="BackgroundColor">#1c1c1c</Color>
<Color x:Key="SurfaceColor">#232323</Color>
<Color x:Key="CardBorderColor">#282828</Color>
<Color x:Key="TableHeaderColor">#353535</Color>
<Color x:Key="NotificationColor">#333333</Color>
<Color x:Key="TextColor">#ffffff</Color>
<Color x:Key="MutedTextColor">#a0a0a0</Color>
```

### SolidColorBrush References
```xml
<SolidColorBrush x:Key="PrimaryBrush" Color="{StaticResource PrimaryColor}"/>
<SolidColorBrush x:Key="PrimaryHoverBrush" Color="{StaticResource PrimaryHoverColor}"/>
<SolidColorBrush x:Key="BackgroundBrush" Color="{StaticResource BackgroundColor}"/>
<SolidColorBrush x:Key="SurfaceBrush" Color="{StaticResource SurfaceColor}"/>
<SolidColorBrush x:Key="CardBorderBrush" Color="{StaticResource CardBorderColor}"/>
<SolidColorBrush x:Key="TextBrush" Color="{StaticResource TextColor}"/>
<SolidColorBrush x:Key="MutedTextBrush" Color="{StaticResource MutedTextColor}"/>
```
