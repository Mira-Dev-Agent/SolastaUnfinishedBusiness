# Finishing Business

Finishing Business is a fork of the Solasta mod [Unfinished Business](https://github.com/EnderWiggin/SolastaUnfinishedBusiness), a [collection of work](https://github.com/EnderWiggin/SolastaUnfinishedBusiness/wiki) from the Solasta modding community. It includes multiclass, feats, classes, subclasses, items, crafting recipes, gameplay options, UI improvements, and more. On top of that, this fork adds multiplayer fixes and options, Level Down, and other changes listed in [ChangelogHistory.txt](SolastaUnfinishedBusiness/ChangelogHistory.txt). The general philosophy is everything is optional to enable, so you can install the mod and then enable the pieces you want. There are some minor bug fixes that are enabled by default.

**About the names:** the mod is called **Finishing Business**, and that's the name shown in game and in Unity Mod Manager. Its mod ID, its folder, its DLL and this repository are still named `SolastaUnfinishedBusiness`, kept from the original mod so that existing installs, settings and saves keep working.

# How to install

1. Install **Unity Mod Manager (UMM) 0.27.10** for Solasta, if you don't have it yet. Get it from the files of [Unity Mod Manager on Nexus Mods](https://www.nexusmods.com/site/mods/21?tab=files); the right download is named `UnityModManager-21-0-27-10-….zip`, and it may be listed under older files.
   - **Only UMM 0.24.0 to 0.27.10 work.** Newer UMM versions aren't compatible with Solasta mods, and the original mod's [installation guide](https://github.com/EnderWiggin/SolastaUnfinishedBusiness/wiki/Install) warns that other versions break your heroes and saves. Versions before 0.24.0 refuse to load the mod (its minimum, `ManagerVersion` in `Info.json`).
   - The releases are built against UMM 0.24.2 and tested in game with UMM 0.27.10.
   - On macOS, use the original mod's [macOS UMM build](https://github.com/EnderWiggin/SolastaUnfinishedBusiness/blob/dev/MacOS/SolastaUnfinishedBusiness-UMM-MACOS.zip).
2. Download `SolastaUnfinishedBusiness.zip` from the latest of this fork's [releases](https://github.com/Mira-Dev-Agent/SolastaUnfinishedBusiness/releases).
3. Extract it into Solasta's `Mods` folder, so you get `Mods\SolastaUnfinishedBusiness\` with `Info.json` and `SolastaUnfinishedBusiness.dll` inside. Keep that folder name.
4. Start Solasta and press <kbd>Ctrl</kbd>+<kbd>F10</kbd> to open Unity Mod Manager. The mod is listed as **Finishing Business**.

Finishing Business replaces Unfinished Business; don't install both. Both use the same mod ID and folder, so extracting over an existing Unfinished Business install upgrades it and keeps your `Settings` folder. In multiplayer, every player needs the same Finishing Business version.

The original mod's [installation guide](https://github.com/EnderWiggin/SolastaUnfinishedBusiness/wiki/Install) has more detail on Unity Mod Manager.

# How to report issues

*  The versions of Solasta and this mod.
*  A list of other mods you have installed [you shouldn't have any].
*  A short description of the bug.
*  A step-by-step procedure to reproduce it.
*  The save, character and log files.

**HINT:** Check the folder *C:\Users\**YOUR_USER_NAME**\AppData\LocalLow\Tactical Adventures\Solasta* for the info we need.

# How to contribute

Do you have a mod you want to see included here? We are happy to take new contributions! The best way to get involved is to:

1. Make a branch off of the `Dev` branch, name it something related to the changes you are making.
2. Commit your edits to your branch.
3. Make sure to test your changes. This is a good opportunity to take screen shots so you can show off the changes.
4. Make a pull request to merge your branch into `Dev`. You can help this process by sharing screen shots to show off the change as well as including an english description of what the change does.
5. Wait for a review. We try to stay on top of this, but it's a hobby.
6. Once the review is done, the changes will get merged in to the `Dev` branch. The `Dev` branch will periodically be tested and merged in to `master` to build releases.

# How to compile

0. Install all required development pre-requisites:
    - [Visual Studio 2022 Community Edition](https://visualstudio.microsoft.com/downloads/)
    - [.NET x64 6.00.300 SDK](https://dotnet.microsoft.com/download/visual-studio-sdks)
1. Download and install [Unity Mod Manager (UMM)](https://www.nexusmods.com/site/mods/21)
2. Execute UMM, Select Solasta, and Install
3. Create the environment variable *SolastaInstallDir* and point it to your Solasta game home folder
    - tip: search for "edit the system environment variables" on windows search bar
4. Open the project and clean the solution. This is key to allow the publicize assembly to be created
5. Build project on "Release Workflow" to create translation data
6. Use "Release Install" or "Debug Install" to have the Mod installed directly to your Game Mods folder

NOTE Unity Mod Manager and this mod template make use of [Harmony](https://go.microsoft.com/fwlink/?linkid=874338)

# How to debug

1. Open Solasta game folder
	* Rename UnityPlayer.dll to UnityPlayer.dll.original
	* Add below entries to *Solasta_Data\boot.config*:
		```
		wait-for-managed-debugger=1
		player-connection-debug=1
		```
2. Download and install [7zip](https://www.7-zip.org/a/7z1900-x64.exe)
3. Download [Unity Editor 2019.4.37](https://download.unity3d.com/download_unity/019e31cfdb15/Windows64EditorInstaller/UnitySetup64-2019.4.37f1.exe)
4. Open Downloads folder
	* Right-click UnitySetup64-2019.4.32f1.exe, 7Zip -> Extract Here
	* Navigate to Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono
		* Copy *UnityPlayer.dll* and *WinPixEventRuntime.dll* to clipboard
	* Navigate to the Solasta game folder
		* Rename *UnityPlayer.dll* to *UnityPlayer.dll.original*
		* Paste *UnityPlayer.dll* and *WinPixEventRuntime.dll* from clipboard
5. You can now attach the Unity Debugger from Visual Studio 2019, Debug -> Attach Unity Debug
