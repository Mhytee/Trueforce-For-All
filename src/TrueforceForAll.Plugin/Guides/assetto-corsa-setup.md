# Assetto Corsa: the TF4ALL CSP Bridge

Assetto Corsa can hand the plugin its own force feedback. The wheel feels exactly as you have tuned it, and the Dynamic OLED display and LIGHTSYNC pattern changes keep working without cutting the force. This needs Custom Shaders Patch (CSP) and a small script, the TF4ALL CSP Bridge, that the plugin installs for you.

## Installing it

Open Settings, find the **Game mods** section, and click **Install** on the TF4ALL CSP Bridge. The plugin also offers the install the first time it sees Assetto Corsa, and the Help guide has the same button. Any of them does the same thing:

1. Finds your Assetto Corsa install.
2. Copies the bridge script into `Assetto Corsa\extension\lua\ffb-postprocess\tf4all`.
3. Selects it under CSP's FFB Tweaks.

Close Content Manager first. It keeps its own copy of the FFB Tweaks page and writes it back over outside changes, so the plugin refuses to install or remove the script while Content Manager is open; close it and click **Retry**.

Then restart Assetto Corsa once, since CSP only reads its scripts at startup. From then on it is automatic: whenever the script is installed the plugin uses the game's own force, and when it is not, the plugin falls back to the USB capture.

Keep your in-game force feedback gain where you like it. The plugin reads the game's finished force (after your gain and every CSP FFB tweak), so your tuning carries through. Use FFBClip or your own judgement to set a gain that fills the range without clipping.

## If the install says a script is already selected

CSP allows only one FFB post-processing script at a time. If you already have one selected and switched on under FFB Tweaks, the install stops and leaves your setup untouched (a script that is selected but switched off does not count). You have two choices:

- **Keep your script.** The plugin still works through its normal USB path, so your force feedback is fine. You just will not get the OLED and pattern-change benefits in Assetto Corsa.
- **Switch to the bridge.** In Content Manager, open Settings, Custom Shaders Patch, FFB Tweaks, and pick `tf4all` as the additional post-processing script (or turn the existing one off), then click **Install** again.

The CSP FFB tweaks themselves (understeer effect, dampers, curbs, and the rest) are not a post-processing script. They keep working either way, because the plugin reads the force after they have been applied.

## Removing it

**Remove** in the Game mods section deletes the script from Assetto Corsa and unselects it in CSP's FFB Tweaks. Your force feedback keeps working through the USB capture; you just lose the OLED and drop-free pattern changes. The script stops loading the next time the game starts.

## Manual install

If the plugin cannot find your Assetto Corsa install (for example a non-Steam copy), install the script by hand:

1. Copy the `tf4all` folder from the plugin's `gamemods\AssettoCorsaCsp` folder into `Assetto Corsa\extension\lua\ffb-postprocess`.
2. In Content Manager, open Settings, Custom Shaders Patch, FFB Tweaks, and select `tf4all` as the additional post-processing script.
3. Start a session and drive.
