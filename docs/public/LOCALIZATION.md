# Localization

StorageMaster ships English (`en-US`), German (`de-DE`) and Spanish (`es-ES`).

The goal is not "translated". It is that a German or Spanish user cannot tell the
app was written in English. That mostly comes down to two things: using the same
words the operating system uses, and using them consistently.

## What gets localized, and what never does

| Surface | Language | Why |
|---|---|---|
| Page titles, navigation, buttons, labels, helper text | Localized | The user reads these. |
| Dialogs, confirmations, warnings, toasts | Localized | Especially these — see *Safety-critical strings*. |
| Cleanup rule names and descriptions | Localized | They are how the user decides what to delete. |
| Empty states, status lines, progress text | Localized | Part of the normal reading path. |
| Accessibility names and help text | Localized | A screen-reader user is a normal user. |
| Log files and the diagnostics export | **Always English** | Read by whoever is debugging, often not the user. |
| CLI output (`--cli`, `--headless`) | **Always English** | Scripted and piped; stable output matters more than locale. |
| Exception messages and stack detail | **Always English** | They end up in logs and issue reports. |

A user-facing error message is localized; the technical detail appended to it is
not. "Die Bereinigung wurde angehalten." is for the user. The exception text after
it is for whoever reads the log.

`LocalizationScopeTests` enforces this: it fails if a localized resource turns up
in a logging or CLI call site, and if a user-facing XAML string is missing a
`x:Uid`.

## Terminology

These are **not** translation choices. They are what Windows itself says, read
directly out of the German MUI resources on a German install
(`shell32.dll.mui`, `windows.storage.dll.mui`, `propsys.dll.mui`). Using anything
else makes the app read as foreign next to File Explorer.

| English | German | Spanish | Source |
|---|---|---|---|
| File | Datei | Archivo | shell32 4130 |
| Folder | Ordner | Carpeta | shell32 4131 |
| Drive | Laufwerk | Unidad | shell32 4122 |
| Disk / volume | Datenträger | Disco | shell32 4126 |
| Recycle Bin | Papierkorb | Papelera de reciclaje | shell32 8964 |
| Size | Größe | Tamaño | shell32 8978 |
| Total size | Gesamtgröße | Tamaño total | shell32 9306 |
| Free space | Freier Speicherplatz | Espacio libre | shell32 9307 |
| Used space | Verwendeter Speicherplatz | Espacio usado | propsys 38652 |
| Percent used | Prozent belegt | Porcentaje usado | shell32 9354 |
| Size on disk | Größe auf Datenträger | Tamaño en disco | propsys 38787 |
| Delete | Löschen | Eliminar | shell32 4147 |
| Move | Verschieben | Mover | shell32 4145 |
| Copy | Kopieren | Copiar | shell32 4146 |
| Rename | Umbenennen | Cambiar nombre | shell32 4148 |
| Properties | Eigenschaften | Propiedades | shell32 4150 |
| Browse | Durchsuchen | Examinar | shell32 9015 |
| Search | Suchen | Buscar | windows.storage 8503 |
| Read-only | Schreibgeschützt | Solo lectura | shell32 8768 |
| Compressed | Komprimiert | Comprimido | shell32 8771 |
| Encrypted | Verschlüsselt | Cifrado | shell32 8772 |
| Hidden | Ausgeblendet | Oculto | propsys 38759 |
| Settings | Einstellungen | Configuración | Windows shell |
| Shortcut | Verknüpfung | Acceso directo | shell32 4153 |

### App vocabulary

Terms Windows has no word for. Fixed here so they never drift between screens.

| English | German | Spanish | Note |
|---|---|---|---|
| Scan (noun) | Überprüfung | Análisis | The run. Not "Scan" — German Windows uses Überprüfung. |
| Scan (verb) | überprüfen | analizar | |
| Cleanup | Bereinigung | Limpieza | Matches Datenträgerbereinigung. |
| Duplicate (noun) | Duplikat | Duplicado | |
| Deduplicate | Duplikate entfernen | Eliminar duplicados | Never a verbed loanword. |
| Quarantine (noun) | Quarantäne | Cuarentena | |
| Quarantine (verb) | in Quarantäne verschieben | poner en cuarentena | |
| Restore | Wiederherstellen | Restaurar | |
| Permanently delete | Endgültig löschen | Eliminar definitivamente | Windows uses *endgültig*. |
| Drive health | Laufwerkszustand | Estado de la unidad | |
| Space map | Speicherbelegung | Mapa de espacio | |
| Treemap | Treemap | Treemap | Kept — it is the chart's name. |
| Snapshot | Momentaufnahme | Instantánea | |
| Scan session | Überprüfungssitzung | Sesión de análisis | |
| Keeper (kept duplicate) | Beizubehaltende Datei | Archivo que se conserva | Never "Keeper". |
| Reclaimable | Freigebbar | Recuperable | |
| Threshold | Schwellenwert | Umbral | |
| Retention | Aufbewahrung | Retención | |
| Accent (colour) | Akzentfarbe | Color de énfasis | Windows personalisation term. |

## German conventions

- **Formal address (`Sie`) throughout.** This is what Windows, Office and
  Microsoft's German style guide use. A destructive-action prompt that says `du`
  next to a Windows dialog that says `Sie` reads as amateurish.
- Buttons are infinitives: **Löschen**, **Überprüfen**, **Abbrechen** — not
  "Löschen Sie".
- Compound nouns rather than English-style noun stacks: *Überprüfungsergebnisse*,
  not *Überprüfung Ergebnisse*.
- German runs roughly 30 % longer than English. Every localized string must be
  checked on screen, not just in the resource file — see *Review*.
- Do not translate: StorageMaster, Windows, SSD, NTFS, exFAT, SHA-256, FFmpeg,
  CSV, JSON, HTML, PNG.

## Spanish conventions

- `es-ES` (Spain). **Archivo**, not *fichero*. **Unidad**, not *disco duro* for a
  drive.
- Formal `usted` implied by using impersonal and infinitive forms; avoid `tú`.
- Buttons are infinitives: **Eliminar**, **Analizar**, **Cancelar**.
- Inverted opening punctuation is required: `¿Eliminar estos archivos?`
- Spanish runs roughly 25 % longer than English.

## Safety-critical strings

Anything that describes deleting, quarantining or permanently removing data.

A mistranslation here is not cosmetic — it can cause a user to destroy files they
meant to keep. These rules are absolute:

- **Recycle Bin and permanent deletion must never be confused.** *Löschen* alone
  is ambiguous in German; use *In den Papierkorb verschieben* when it is
  recoverable and *Endgültig löschen* when it is not.
- Never soften a warning in translation. If the English says data cannot be
  recovered, the translation says so just as plainly.
- Never make a translated confirmation shorter or friendlier than its English
  original.

These strings carry a `Safety_` key prefix and are reviewed separately from the
rest.

## Adding or changing a string

1. Add the key to `Strings/en-US/Resources.resw` first. English is the source.
2. Add the same key to `de-DE` and `es-ES`. Missing keys fail the build tests.
3. Use the glossary. If a term is not in it and is not obvious, add it to the
   glossary rather than deciding per string.
4. Keep placeholders identical across languages (`{0}`, `{1}`). Reorder the words
   around them, never renumber them.

## Review

Automated checks (`LocalizationTests`) cover key parity, placeholder parity,
untranslated leftovers and the scope rules above. They cannot see layout.

Before a language ships, run the app in it and read the actual screens. German
in particular will overflow controls that fit English comfortably.
