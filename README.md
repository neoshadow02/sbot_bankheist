# 🏦 Streamer.bot Bankheist Minigame (Multilingual)

Ein mehrsprachiges Bankraub-Minigame für **Streamer.bot** (`C# Inline Code`) mit Unterstützung für **Streamer.bot-interne Punkte** und **StreamElements-Punkte**, rollenbasierten Gewinnchancen, automatischer Timer-Steuerung und frei anpassbaren Texten.

---

## ✨ Features

- **8 eingebaute Sprachen + Custom-Modus:** Deutsch (`de`), Englisch (`en`), Spanisch (`es`), Französisch (`fr`), Portugiesisch BR (`pt-br`), Türkisch (`tr`), Niederländisch (`nl`), Polnisch (`pl`) sowie `custom` für komplett eigene Texte.
- **Eigene Währungsbezeichnung:** Über `heistWordPoints` (z. B. `Rupees`, `Coins`, `Taler`) wird deine Kanalwährung automatisch auch in den eingebauten Sprachen verwendet.
- **2 Punktesysteme wählbar:**
  - **Streamer.bot** (persistente Twitch-User-Variablen per `userId`, mit Fallback auf `userName`)
  - **StreamElements** (über die offizielle StreamElements-API mit Channel-ID & JWT-Token)
- **Rollenbasierte Erfolgschancen:** Separate Gewinnchancen für Broadcaster, Moderatoren, VIPs und normale Zuschauer.
- **All-In & Status-Befehl:** Unterstützt `!heist all` / `!heist allin` (automatisch auf `heistMaxBet` begrenzt) sowie `!heist status`.
- **Self-Healing Cooldown:** Falls ein Timer durch einen Neustart hängen bleibt, heilt sich der Cooldown automatisch anhand des UTC-Zeitstempels selbst.
- **Automatischer Refund:** Treten zu wenige Teilnehmer bei (`heistMinParticipants`), wird der Raub abgebrochen und alle Einsätze werden zurückerstattet.

---

## 📋 Voraussetzungen

- **Streamer.bot v1.0+** für den fertigen Import über [import.sb](import.sb) *(exportiert aus v1.0.7; bei manueller Einrichtung von [main.cs](main.cs) reicht v0.2.4+)*
- Verbundenes **Twitch-Konto** in Streamer.bot
- Optional für StreamElements: **Channel-ID** und **JWT-Token** aus deinem StreamElements-Dashboard

---

## 🛠️ Schritt-für-Schritt-Installation

### Option A: Schnellstart über `import.sb` (empfohlen)

Die Datei [import.sb](import.sb) enthält bereits das komplette Paket:
- Alle **7 Actions** (Code, Methoden & Settings)
- Beide **Timed Actions (Timer)** inkl. fertig hinterlegter Timer-IDs
- Den Chat-Command **`!raub`** *(kann jederzeit z. B. in `!heist` umbenannt werden)*
- Den aktuellen C#-Code aus [main.cs](main.cs)

**So installierst du es in 4 einfachen Schritten:**
1. **Importieren:** Öffne die Datei [import.sb](import.sb), kopiere den kompletten Inhalt, klicke in Streamer.bot oben auf **Import**, füge den String in das Feld **Import String** ein und bestätige mit **Import**.
2. **Wichtig direkt nach dem Import (Code kompilieren):**
   - Öffne die Action `[MINIGAME] Bankheist Multilingual - Code - Main`.
   - Öffne per Doppelklick die C#-Sub-Action `[Minigame] Bankheist - Main Controller`.
   - Klicke im Tab **References** auf **Find Refs**, damit alle benötigten DLLs (wie `Newtonsoft.Json.dll`) automatisch eingebunden werden.
   - Klicke anschließend auf **Save and Compile**. *(Erst danach sind auch die Methoden `ExecuteHeist` und `ResetCooldown` für die Timer verfügbar!)*
3. **Einstellungen anpassen:**
   - **Punktesystem wählen:** In `[MINIGAME] Bankheist Multilingual - Settings - Config` die gewünschte Sub-Action aktivieren (`Settings - Streamer.bot` ist standardmäßig aktiv; für StreamElements stattdessen `Settings - Streamelements` aktivieren und dort Channel-ID & JWT eintragen).
   - **Sprache & Währung:** In `Settings - Language` die Sprache (`de`, `en`, …, `custom`) wählen und in `Settings - Config` ggf. `heistWordPoints` (Standard: `Rupees`), Einsätze, Chancen oder Zeiten anpassen.
   - **Command-Name (optional):** Unter **Commands** kannst du `!raub` beliebig umbenennen (z. B. in `!heist`).
4. **Erst-Setup ausführen:** Schreibe als Broadcaster oder Mod einmalig `!raub setup` (bzw. `!heist setup`) in den Twitch-Chat – fertig!

---

### Option B: Manuelle Einrichtung (4 Schritte)

### Schritt 1: Zwei Timed Actions (Timer) erstellen

Gehe in Streamer.bot auf **Settings → Timed Actions** und erstelle zwei Timer (**beide zunächst deaktiviert lassen!**):

| Timer-Name | Empfohlenes Intervall | Enabled |
| :--- | :--- | :--- |
| `[MINIGAME] Bankheist Multilingual - Timer - Join` | `60` (oder `120`) Sekunden | ❌ Nein |
| `[MINIGAME] Bankheist Multilingual - Timer - Cooldown` | `300` (oder `600`) Sekunden | ❌ Nein |

> **Wichtig:** Kopiere dir per Rechtsklick auf jeden Timer die jeweilige **Timer-ID (GUID)** (z. B. `0d960e11-bfcd-4ed0-8401-84057c32accb`). Nur mit eingetragener Timer-ID kann das Skript das Timer-Intervall automatisch anpassen!

---

### Schritt 2: Actions in Streamer.bot anlegen

Erstelle unter **Actions** eine Gruppe namens `[MINIGAME] Bankheist Multilingual` mit folgenden **7 Actions**:

1. `[MINIGAME] Bankheist Multilingual - Settings - Streamer.bot`
2. `[MINIGAME] Bankheist Multilingual - Settings - Streamelements`
3. `[MINIGAME] Bankheist Multilingual - Settings - Config`
4. `[MINIGAME] Bankheist Multilingual - Settings - Language`
5. `[MINIGAME] Bankheist Multilingual - Code - Main`
6. `[MINIGAME] Bankheist Multilingual - Methods - Execute`
7. `[MINIGAME] Bankheist Multilingual - Methods - Cooldown`

---

### Schritt 3: Sub-Actions und Trigger einrichten

#### 3.1 `Settings - Streamer.bot` (für interne Streamer.bot-Punkte)
Füge folgende **Core → Arguments → Set Argument**-Sub-Actions hinzu:
- `heistPointsSystem` = `streamerbot`
- `heistPointsVariable` = `points` *(Name deiner User-Variable, z. B. `points` oder `userPoints`)*

#### 3.2 `Settings - Streamelements` (für StreamElements-Punkte)
Füge folgende **Set Argument**-Sub-Actions hinzu:
- `heistPointsSystem` = `streamelements`
- `heistStreamElementsChannelId` = `DEINE_CHANNEL_ID`
- `heistStreamElementsJwt` = `DEIN_JWT_TOKEN`

> ⚠️ **Sicherheitshinweis:** Zeige deinen StreamElements **JWT-Token** niemals im Stream und teile keinen Export, solange dein Token dort eingetragen ist!

#### 3.3 `Settings - Config`
1. Füge beide Punkte-Settings als **Core → Action → Run Action** hinzu und **aktiviere nur das gewünschte System** (das andere deaktivieren):
   - `Run Action` → `[MINIGAME] Bankheist Multilingual - Settings - Streamelements` (**Run Action Immediately: ✅**)
   - `Run Action` → `[MINIGAME] Bankheist Multilingual - Settings - Streamer.bot` (**Run Action Immediately: ✅**)
2. Füge danach die **Set Argument**-Sub-Actions für deine gewünschten Spieleinstellungen hinzu:
   - `heistDefaultPoints` = `1000`
   - `heistMinBet` = `50`
   - `heistMaxBet` = `2000`
   - `heistMinParticipants` = `1`
   - `heistWinMultiplier` = `1.8` *(auch `"1.8"` oder `1,8` wird automatisch erkannt)*
   - `heistJoinTime` = `120`
   - `heistCooldown` = `600`
   - `heistChanceBroadcaster` = `95`
   - `heistChanceMod` = `80`
   - `heistChanceVip` = `70`
   - `heistChanceUser` = `50`
   - `heistJoinTimerId` = `<DEINE JOIN-TIMER-ID>`
   - `heistCooldownTimerId` = `<DEINE COOLDOWN-TIMER-ID>`
   - `heistPointsApiTimeoutMs` = `7000`
   - `heistDebugApiErrors` = `False`
   - `heistWordPoints` = `Rupees` *(optional – leer lassen für die Standardwährung der gewählten Sprache)*

#### 3.4 `Settings - Language`
1. **Set Argument:** `heistLanguage` = `de` *(oder `en`, `es`, `fr`, `pt-br`, `tr`, `nl`, `pl`, `custom`)*
2. *(Optional)* Einen Ordner mit **Set Argument**-Sub-Actions für alle eigenen Texte anlegen (greift nur, wenn `heistLanguage` auf `custom` steht – siehe Abschnitt **Custom-Texte** weiter unten).

#### 3.5 `Code - Main`
- **Trigger:** **Core → Commands → Command Triggered**
  - Erstelle unter **Commands** deinen Wunschbefehl (z. B. `!heist` oder `!raub`) und wähle ihn hier als Trigger aus.
- **Sub-Actions (Reihenfolge beachten!):**
  1. **Core → Action → Run Action:** `[MINIGAME] Bankheist Multilingual - Settings - Config` (**Run Action Immediately: ✅**)
  2. **Core → Action → Run Action:** `[MINIGAME] Bankheist Multilingual - Settings - Language` (**Run Action Immediately: ✅**)
  3. **Core → C# → Execute C# Code:**
     - Gib der Sub-Action einen eindeutigen Namen, z. B. `[Minigame] Bankheist - Main Controller`
     - Kopiere den kompletten Inhalt aus [main.cs](main.cs) hinein
     - Klicke im Tab **References** auf **Find Refs**, damit alle benötigten DLLs (u. a. `Newtonsoft.Json.dll` und `System.dll`) automatisch eingebunden werden
     - Klicke anschließend auf **Save and Compile**

> **Wichtig:** Bei den `Run Action`-Aufrufen für `Settings - Config`, `Settings - Language` sowie den Points-Settings **muss** `Run Action Immediately` aktiviert sein! Sonst laufen die Settings-Actions erst *nach* dem C#-Code.

#### 3.6 `Methods - Execute` (Auswertung nach Ablauf der Beitrittszeit)
- **Trigger:** **Core → Timed Actions →** `[MINIGAME] Bankheist Multilingual - Timer - Join`
- **Sub-Action:** **Core → C# → Execute Method**
  - Wähle die C#-Sub-Action `[Minigame] Bankheist - Main Controller`
  - Methode: `ExecuteHeist`
  - *(Hier müssen **keine** Settings-Actions erneut geladen werden – `Execute()` synchronisiert alle Werte automatisch in den globalen Speicher.)*

#### 3.7 `Methods - Cooldown` (Cooldown-Ende & Erinnerung)
- **Trigger:** **Core → Timed Actions →** `[MINIGAME] Bankheist Multilingual - Timer - Cooldown`
- **Sub-Action:** **Core → C# → Execute Method**
  - Wähle die C#-Sub-Action `[Minigame] Bankheist - Main Controller`
  - Methode: `ResetCooldown`

---

### Schritt 4: Erst-Setup ausführen

Schreibe als **Broadcaster** oder **Moderator** im Twitch-Chat:

```text
!heist setup
```
*(bzw. `!raub setup`, je nach deinem konfigurierten Command – alternativ auch `status setup` oder direkt über „Test“ im C#-Editor)*

Das Setup speichert alle Grundeinstellungen, setzt laufende Zustände sauber zurück, stellt die Timer-Intervalle ein und deaktiviert beide Timer.

> **Gut zu wissen:** Spätere Änderungen in deinen Settings-Actions (`Settings - Config`, `Settings - Language`, usw.) werden beim nächsten normalen Chat-Befehl automatisch übernommen – ein erneutes `!heist setup` ist danach nur nötig, wenn du einen hängenden Heist/Cooldown manuell zurücksetzen möchtest.

---

## 💬 Chat-Befehle

*(Beispiel mit `!heist` – das Skript verwendet automatisch den ausgelösten Command, z. B. auch `!raub`)*

| Befehl | Berechtigung | Funktion |
| :--- | :--- | :--- |
| `!heist <einsatz>` | Alle | Startet einen neuen Raubzug oder tritt einer offenen Lobby mit `<einsatz>` bei |
| `!heist all` / `!heist allin` | Alle | Setzt alle verfügbaren Punkte (maximal jedoch `heistMaxBet`) |
| `!heist status` | Alle | Zeigt den aktuellen Status (offene Lobby inkl. Restzeit & eigenem Einsatz, Cooldown oder bereit) |
| `!heist setup` | Broadcaster / Mod | Initialisiert alle Variablen, setzt Lobby/Cooldown zurück und konfiguriert die Timer |

---

## ⚙️ Konfigurations-Referenz

| Variable / Argument | Standardwert | Beschreibung |
| :--- | :--- | :--- |
| `heistLanguage` | `de` | Sprache: `de`, `en`, `es`, `fr`, `pt-br`, `tr`, `nl`, `pl` oder `custom` |
| `heistPointsSystem` | `streamerbot` | Punktesystem: `streamerbot` oder `streamelements` (Aliase: `se`, `elements`) |
| `heistPointsVariable` | `userPoints` | Name der Streamer.bot User-Variable (nur bei `streamerbot`) |
| `heistDefaultPoints` | `1000` | Startpunkte für neue User ohne bestehende Variable (nur bei `streamerbot`) |
| `heistMinBet` | `50` | Minimaler Einsatz |
| `heistMaxBet` | `10000` | Maximaler Einsatz (auch Obergrenze für `all` / `allin`) |
| `heistMinParticipants` | `1` | Mindestanzahl Teilnehmer (bei weniger Spielern wird abgebrochen & erstattet) |
| `heistWinMultiplier` | `1.8` | Gewinn-Multiplikator (Punkt, Komma und Anführungszeichen wie `"1.8"` werden unterstützt) |
| `heistJoinTime` | `60` | Beitrittszeit in Sekunden |
| `heistCooldown` | `300` | Sperrzeit nach einem Raubzug in Sekunden (`0` = kein Cooldown) |
| `heistChanceBroadcaster` | `95` | Erfolgschance für Broadcaster in % (`0`–`100`) |
| `heistChanceMod` | `80` | Erfolgschance für Moderatoren in % (`0`–`100`) |
| `heistChanceVip` | `70` | Erfolgschance für VIPs in % (`0`–`100`) |
| `heistChanceUser` | `50` | Erfolgschance für normale Zuschauer in % (`0`–`100`) |
| `heistJoinTimerId` | `""` | GUID des Join-Timers (empfohlen, damit `heistJoinTime` automatisch gesetzt wird) |
| `heistCooldownTimerId` | `""` | GUID des Cooldown-Timers (empfohlen, damit `heistCooldown` automatisch gesetzt wird) |
| `heistWordPoints` | *(leer)* | Eigener Name für die Währung (z. B. `Rupees`). Leer = Standardwort der Sprache |
| `heistStreamElementsChannelId` | `""` | StreamElements Account-/Channel-ID (nur bei `streamelements`) |
| `heistStreamElementsJwt` | `""` | StreamElements JWT-Token (nur bei `streamelements`) |
| `heistPointsApiTimeoutMs` | `7000` | HTTP-Timeout in Millisekunden für die StreamElements-API |
| `heistDebugApiErrors` | `false` | `true` aktiviert ausführliche Debug-Logs (`[Bankheist Debug]`) im Streamer.bot-Log |

---

## 🌍 Sprachen & Custom-Texte

### Eingebaute Sprachen
`de` (Deutsch), `en` (English), `es` (Español), `fr` (Français), `pt-br` / `pt` (Português BR), `tr` (Türkçe), `nl` (Nederlands), `pl` (Polski).

### Eigene Texte (`heistLanguage` = `custom`)
Setze `heistLanguage` auf `custom` und definiere beliebige der folgenden 22 Variablen (fehlende Texte fallen automatisch auf Deutsch zurück):

| Key | Verfügbare Platzhalter |
| :--- | :--- |
| `heistMsgSetupStarted` | `%pointsWord%` |
| `heistMsgSetupCompleted` | `%pointsWord%` |
| `heistMsgSetupDenied` | `%user%`, `%command%` |
| `heistMsgStart` | `%user%`, `%remainingTime%`, `%command%`, `%pointsWord%` |
| `heistMsgJoined` | `%user%`, `%amount%`, `%command%`, `%pointsWord%` |
| `heistMsgReminder` | `%command%`, `%min%`, `%max%`, `%pointsWord%` |
| `heistMsgNotEnoughPoints` | `%user%`, `%currentPoints%`, `%command%`, `%pointsWord%` |
| `heistMsgInvalidBet` | `%min%`, `%max%`, `%command%`, `%pointsWord%` |
| `heistMsgCooldown` | `%cooldown%`, `%command%` |
| `heistMsgAlreadyIn` | `%user%`, `%command%` |
| `heistMsgNotEnoughPlayers` | `%minPlayers%`, `%command%` |
| `heistMsgProgress` | `%command%` |
| `heistMsgSuccess` | `%survivors%`, `%caught%`, `%payout%`, `%command%`, `%pointsWord%` |
| `heistMsgPartial` | `%survivors%`, `%caught%`, `%payout%`, `%command%`, `%pointsWord%` |
| `heistMsgFailed` | `%survivors%`, `%caught%`, `%payout%`, `%command%`, `%pointsWord%` |
| `heistMsgStatusActive` | `%remainingTime%`, `%userBet%`, `%totalBet%`, `%players%`, `%user%`, `%command%`, `%pointsWord%` |
| `heistMsgStatusCooldown` | `%cooldown%`, `%command%` |
| `heistMsgStatusIdle` | `%command%`, `%user%`, `%min%`, `%max%`, `%pointsWord%` |
| `heistMsgNobodySurvivors` | *(Text für „Niemand“ bei Überlebenden)* |
| `heistMsgNobodyCaught` | *(Text für „Niemand“ bei Gefassten)* |
| `heistMsgPointsApiError` | `%user%`, `%command%` |
| `heistMsgStatusNotJoined` | *(Text für „Nicht dabei“ im Status)* |

---

## 🪙 StreamElements-Daten finden

Falls du `streamelements` statt `streamerbot` als Punktesystem verwenden möchtest:
1. Melde dich auf [streamelements.com](https://streamelements.com/) an.
2. Klicke oben rechts auf dein Profilbild → **Account Settings** → Tab **Channels**.
3. Klicke auf **Show secrets**:
   - **Account ID** → in `heistStreamElementsChannelId` eintragen.
   - **JWT Token** → in `heistStreamElementsJwt` eintragen.

---

## 🔧 Troubleshooting

- **Der Raubzug startet, aber endet nicht (kein Ergebnis im Chat):**
  - Prüfe, ob der Timer `[MINIGAME] Bankheist Multilingual - Timer - Join` existiert und als Trigger bei `Methods - Execute` eingetragen ist.
  - Prüfe, ob `heistJoinTimerId` exakt zur Timer-ID passt.
- **Änderungen in `Settings - Config` oder `Settings - Language` greifen nicht:**
  - Stelle sicher, dass bei den `Run Action`-Sub-Actions in `Code - Main` und `Settings - Config` jeweils **Run Action Immediately** angehakt ist.
  - Achte darauf, dass `%heistWordPoints%` nicht doppelt an zwei Stellen mit unterschiedlichen Werten gesetzt wird.
- **Fehlermeldung „Das Punktesystem ist gerade nicht erreichbar“:**
  - Setze `heistDebugApiErrors` temporär auf `true`, löse den Command erneut aus und prüfe das Streamer.bot-Log (`[Bankheist]` / `[Bankheist Debug]`) auf die genaue Fehlermeldung (z. B. falsche Channel-ID oder abgelaufener JWT-Token).
- **Kompilierfehler oder fehlende Methoden (`ExecuteHeist` / `ResetCooldown`) nach dem Import:**
  - Öffne in `Code - Main` die C#-Sub-Action → Tab **References** → klicke auf **Find Refs** (damit u. a. `Newtonsoft.Json.dll` und `System.dll` eingebunden werden) → danach auf **Save and Compile**.

---

## 📄 Lizenz

Dieses Projekt steht unter der Lizenz in [LICENSE](LICENSE).
