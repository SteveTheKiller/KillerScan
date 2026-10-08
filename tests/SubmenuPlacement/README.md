# Submenu placement regression

Run `powershell -File tests/SubmenuPlacement/Run.ps1` from this checkout. A .NET 10 Windows SDK, .NET Framework 4.8 targeting support and an interactive Windows session are required. The runner selects the matching product WPF runtime. The fixture parses this checkout's actual menu templates and 13 palettes, with no product startup or user data access. Source-only assertions do not establish success: it opens first and nested popups, measures their visible borders in screen coordinates, exercises routed keyboard navigation, and verifies the hit-test corridor and native cascade direction at both screen edges. PNGs and a JSON report remain in a fresh temporary directory; the helper build is removed.

The family keeps its existing chrome and shadow halo. Placement targets the complete opening MenuItem, with WPF Aero's row-relative -2/-3 DIP offsets and the standard PART_Popup contract. Popup.cs uses child render bounds and transforms when placing the popup; its outer margin already contributes to that transform, so adding separate shadow compensation pulled the visible cards inward. Mild overlap remains intentional. Existing content insets can make the visible border overlap larger at nested levels.

Primary sources:

- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/popup-placement-behavior
- https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/Themes/XAML/MenuItem.xaml
- https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/Popup.cs
- https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/MenuItem.cs

The runner crosses LTR/RTL with both MenuDropAlignment values by changing only WPF's cached value inside its disposable process and restoring it. It never writes system settings. Routed mouse events check WPF's actual hover timer and travel into the submenu; no global input is injected. The 100/125/150/200 percent layout transforms test scaled geometry, not actual per-monitor DPI transitions. Physical pointer travel, hardware hover behavior, actual OS DPI changes and mixed-monitor operation remain separate interactive checks. The palette resources use geometry fallbacks in the fixture; this is a live-template test, not a claim that the complete product ThemeManager or app window has been exercised.

The synthetic fixture keeps its root ContextMenu open and creates a host that does not activate on show, so desktop focus changes cannot dismiss its test hierarchy. It awaits actual Popup.Closed events before reopening after animation. Outside-click dismissal requires the separate physical-pointer check.

Native fixture windows are DWM-cloaked immediately after creation. They retain real HWND layout and placement but do not appear in desktop composition. Only handles obtained from this fixture's new controls are used; no existing application window or system setting is changed. PNGs are rendered from those actual WPF popup roots.

`-Windowless` instantiates and measures the real templates across all 13 palettes and four scales, checking resolved row targeting, native popup registration and offsets. It never shows a window or opens a popup. This mode supports parallel desktop work but does not replace the live native placement and input checks.

`-SourcePath` permits a saved pre-change XAML or a deliberate negative-control mutation. The old templates must fail these attachment and native-part checks.
