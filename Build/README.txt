
REQUIREMENTS  /  需要预装的东西

Which package you downloaded decides what you need:

  * "SingleFile_JASM_vX.Y.Z.zip"  -> NOTHING to install. Unzip it and double-click the exe inside.
  * "JASM_vX.Y.Z.7z" (and "SelfContained_..." is fine too) -> the plain .7z folder package is
    framework-dependent. If you have never installed the runtimes below, Windows will show
    "Required components of the Windows App Runtime are missing" when you start JASM.

    Install both of these first, then start JASM again:
      - .NET 9 Desktop Runtime    https://dotnet.microsoft.com/download/dotnet/9.0
      - Windows App Runtime 1.7   https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads

    Or skip all of that and use the SingleFile zip above instead - it needs nothing preinstalled.

下载的是哪个包，决定了你要不要装东西：

  * "SingleFile_JASM_vX.Y.Z.zip"：什么都不用装，解压后双击里面的 exe 即可。
  * "JASM_vX.Y.Z.7z"（文件夹版，框架依赖）：需要先装上面那两个运行时，否则启动时会弹
    "Required components of the Windows App Runtime are missing"。装完再启动 JASM。
    不想装的话，改用上面的 SingleFile 那个 zip —— 它不需要任何预装。


To start JASM run "JASM - Just Another Skin Manager.exe" inside JASM folder. You will then be prompted by a startup screen that will guide you.

JASM is a Skin Manager, not a mod injector. It helps manage mod folders by organizing and displaying them. 3Dmigoto is the loader responsible for injecting mods into Genshin.

Updating from previous versions: 

From Version 1.4.3 and upwards -> Click the update button at the bottom of the settings page.

						OR

1. Download JASM.
2. Delete the old JASM folder and replace it with the new one. 

Personal settings are stored in "C:\Users\<username>\AppData\Local\JASM\ApplicationData" or in the mods themselves

If you have any questions or suggestions, please post them on https://gamebanana.com/tools/14574 or https://github.com/Jorixon/JASM

JASM is distributed under the terms of the GNU General Public License v3.0 more details https://github.com/Jorixon/JASM?tab=GPL-3.0-1-ov-file#readme

This is mostly a personal project but if you'd like, you can support me at https://www.buymeacoffee.com/jorixonjasm

Hope you enjoy using JASM!
