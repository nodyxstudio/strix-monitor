# Composants tiers / Third-party components

Strix Monitor (licence MIT) s'appuie sur les bibliothèques suivantes. Elles sont incluses, **sans modification**, dans l'exécutable publié.

Strix Monitor (MIT license) uses the following libraries. They are bundled, **unmodified**, in the released executable.

| Composant | Licence | Source |
|---|---|---|
| LibreHardwareMonitorLib 0.9.6 | Mozilla Public License 2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit | Mozilla Public License 2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | Mozilla Public License 2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| HidSharp | Apache License 2.0 (© James F. Bellinger) | https://software.seekye.com/hidsharp |
| .NET 8 runtime, WPF, Windows Forms, System.Management | MIT (© .NET Foundation and Contributors) | https://github.com/dotnet |

Le code source de ces composants est disponible aux adresses ci-dessus, sous leurs licences respectives.

The source code of these components is available at the addresses above, under their respective licenses.

## PawnIO

Le pilote [PawnIO](https://pawnio.eu) (par namazso) n'est **pas** inclus dans Strix Monitor. S'il n'est pas déjà présent, l'installateur télécharge l'installateur officiel depuis
`https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe`, vérifie son empreinte SHA-256
(`1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`, la même que le paquet winget `namazso.PawnIO`), puis le lance.

The [PawnIO](https://pawnio.eu) driver (by namazso) is **not** bundled. If it is missing, the setup downloads the official installer from the URL above, checks its SHA-256 checksum, then runs it.
