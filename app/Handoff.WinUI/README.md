# Handoff WinUI

Empty WinUI 3 shell for the Handoff Windows desktop UI.

## Layout

- `Components/Controls`: reusable `UserControl` and custom control components.
- `Components/Layouts`: shell, navigation, and layout components shared by pages.
- `Components/Dialogs`: `ContentDialog`, flyout, and modal UI components.
- `Pages`: top-level screens displayed by the main window frame.
- `ViewModels`: MVVM state and commands for pages/components.
- `Services`: UI-facing services such as navigation, settings, and background-service adapters.
- `Resources`: XAML resources, styles, theme dictionaries, and localization files.
- `Assets`: images, icons, logos, and other UI media.

Start new UI pieces in the narrowest folder that matches their responsibility. Shared controls belong under `Components`; full screens belong under `Pages`.
