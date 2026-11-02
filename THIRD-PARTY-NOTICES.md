# Third-party notices

What `acb` and the DemoViewer.NET plugin include from others, and under which terms.

## EFF large word list

`src/CS2OpenDev.ConfigManager.Core/Archive/eff-large-wordlist.txt` holds the words of the Electronic Frontier
Foundation's large word list for passphrases, in its order, without its dice numbers. Generated seal passphrases
are drawn from it.

- Source: https://www.eff.org/dice (https://www.eff.org/files/2016/07/18/eff_large_wordlist.txt, SHA-256
  `addd35536511597a02fa0a9ff1e5284677b8883b83e986e43f15a3db996b903e` as downloaded 2026-11-02)
- Copyright: Electronic Frontier Foundation
- License: Creative Commons Attribution 3.0 United States, https://creativecommons.org/licenses/by/3.0/us/
- Changes: the dice numbers are removed, and two comment lines naming the source and license are added.

## Konscious.Security.Cryptography

Argon2id (`Konscious.Security.Cryptography.Argon2`) and BLAKE2b (`Konscious.Security.Cryptography.Blake2`),
which make a sealed backup's key. (c) 2024 Keef Aragon. MIT License.
https://github.com/kmaragon/Konscious.Security.Cryptography

## Terminal.Gui

The wizard's terminal interface (`acb` only). By Miguel de Icaza, Tig Kindel, BDisp and contributors. MIT License.
https://github.com/gui-cs/Terminal.Gui
