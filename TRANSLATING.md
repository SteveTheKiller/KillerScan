# Adding or Improving a KillerScan Translation

The translatable strings live in `Strings/` - one XAML `ResourceDictionary` per language.

## File format

Each language is a single XAML `ResourceDictionary` in the `Strings/` folder, named with the BCP 47 tag:

- `Strings/en-US.xaml` - English (US) - the base; every other file layers over it
- `Strings/bn.xaml` - Bengali
- `Strings/cs-CZ.xaml` - Czech
- `Strings/de-DE.xaml` - German
- `Strings/es.xaml` - Spanish
- `Strings/fr-FR.xaml` - French
- `Strings/hu-HU.xaml` - Hungarian
- `Strings/ja-JP.xaml` - Japanese
- `Strings/pl-PL.xaml` - Polish
- `Strings/tr-TR.xaml` - Turkish
- `Strings/zh-CN.xaml` - Simplified Chinese
- `Strings/zh-TW.xaml` - Traditional Chinese

## How to contribute

### Improving an existing translation

1. Open the file for your language and edit the text **between** the `<sys:String>` tags.
2. **Never change the `x:Key` values** - the app looks strings up by key at runtime.
3. Open a pull request.

### Adding a new language

1. Copy `Strings/en-US.xaml`, rename it to your BCP 47 tag, translate the values.
2. You don't have to translate every key - any key you leave out falls back to English, so a partial file is fine.
3. A new language also needs the maintainer to wire it in (the `Locale` enum + loader in `Services/LocaleManager.cs` and the language menu), so note that in your PR.

## Rules

- **Never change `x:Key` values.**
- **Keep acronyms and proper names as-is:** IP, MAC, DNS, RDP, SSH, CSV, HTML, SHA-256, OUI, Wireshark, Killer Tools.
- **Don't translate device-type names** (Router, NAS, Printer, etc.) - those are matched in code and color-coded in the table; translating them breaks both.
- **Keep any `{0}` / `{1}` format placeholders** intact and in a sensible order for your language.
- **Keep XML entities** (`&amp;`, `&#xNNNN;`) as they are.
- **Use plain hyphens (`-`), never em or en dashes,** even where your language's typography would normally call for one. This is a house style rule applied across every app and every language, so it deliberately overrides local convention. If you think it is wrong for your language, say so in the pull request rather than working around it.
- The file must be valid XML - paste it into any XML validator to check.

## Notes

- Missing keys always fall back to `Strings/en-US.xaml`, so you never have to keep a file fully in sync.
- The Chinese and Bengali files are a machine-assisted first pass - native corrections are very welcome.

## Questions

Open a GitHub issue or comment on your pull request.
