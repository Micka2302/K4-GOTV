<a name="readme-top"></a>

![GitHub tag (with filter)](https://img.shields.io/github/v/tag/KitsuneLab-Development/K4-GOTV?style=for-the-badge&label=Version)
![GitHub Repo stars](https://img.shields.io/github/stars/KitsuneLab-Development/K4-GOTV?style=for-the-badge)
![GitHub issues](https://img.shields.io/github/issues/KitsuneLab-Development/K4-GOTV?style=for-the-badge)
![GitHub](https://img.shields.io/github/license/KitsuneLab-Development/K4-GOTV?style=for-the-badge)
![GitHub all releases](https://img.shields.io/github/downloads/KitsuneLab-Development/K4-GOTV/total?style=for-the-badge)
![GitHub last commit (branch)](https://img.shields.io/github/last-commit/KitsuneLab-Development/K4-GOTV/dev?style=for-the-badge)

<!-- PROJECT LOGO -->
<br />
<div align="center">
  <h1 align="center">KitsuneLab©</h1>
  <h3 align="center">CS2 Advanced GOTV</h3>
  <a align="center">Automatically handles GOTV recording, able to crop demos for every round separately. Sends the recorded demo as zipped to Discord Webhook as attachment or uploads via FTP when configured. Customizable webhook, avatar, bot name, embed and more. Automatically stops recording on idle to conserve resources.</a>

  <p align="center">
    <br />
    <a href="https://github.com/KitsuneLab-Development/K4-GOTV/releases">Download</a>
    ·
    <a href="https://github.com/KitsuneLab-Development/K4-GOTV/issues/new?assignees=KitsuneLab-Development&labels=bug&projects=&template=bug_report.md&title=%5BBUG%5D">Report Bug</a>
    ·
    <a href="https://github.com/KitsuneLab-Development/K4-GOTV/issues/new?assignees=KitsuneLab-Development&labels=enhancement&projects=&template=feature_request.md&title=%5BREQ%5D">Request Feature</a>
  </p>
</div>

### Support My Work

Your support keeps my creative engine running and allows me to share knowledge with the community. Thanks for being part of my journey.

<p align="center">
<a href="https://www.buymeacoffee.com/k4ryuu">
<img src="https://img.buymeacoffee.com/button-api/?text=Support Me&emoji=☕&slug=k4ryuu&button_colour=FF5F5F&font_colour=ffffff&font_family=Inter&outline_colour=000000&coffee_colour=FFDD00" />
</a>
</p>

<!-- ABOUT THE PROJECT -->

### Placeholder values

They all should be used in the format `{placeholder}`

- `map` - Represents the name of the server map.
- `date` - Represents the current date in the format "yyyy-MM-dd".
- `time` - Represents the current time in the format "HH:mm:ss".
- `timedate` - Represents the current date and time in the format "yyyy-MM-dd HH:mm:ss".
- `length` - Represents the duration of something, likely a demo length, formatted as "mm:ss".
- `round` - Represents the total number of rounds played in a game.
- `player_count` - Represents the total count of players.



### Dependencies

To use this server addon, you'll need the following dependencies installed:

- [**CounterStrikeSharp 1.0.375**](https://github.com/roflmuffin/CounterStrikeSharp/releases/tag/v1.0.375) or newer. Version 1.0.375 includes compatibility fixes for CS2 1.41.8.2.
- .NET 10 (included in the CounterStrikeSharp **with-runtime** download).
- [**Metamod:Source 2.x build 1467 or newer**](https://docs.cssharp.dev/docs/guides/getting-started.html), with KHook support.

### Updating and checking demo recording

Stop the server, update its full CounterStrikeSharp installation (including native binaries, API and gamedata), and copy the plugin's `counterstrikesharp` folder into `game/csgo/addons/`. Updating only `K4-GOTV.dll` does not update CounterStrikeSharp itself. Restart the server after installing both.

Ensure `tv_enable 1` is configured before loading the map, and use `tv_autorecord 0` when K4-GOTV manages recording. Enable `auto-record.enabled` in the plugin configuration. If CSTV was enabled during a running map, reload the map before testing. Use `meta list`, `css_plugins list` and `tv_status` in the server console to check the installation.

Relative recording paths can resolve under `game/csgo/addons/metamod/`, causing `CDemoFile::Open: couldn't open file discord_demos/... for writing` when that subdirectory does not exist there. Starting with 2.1.5, K4-GOTV passes the absolute path of the configured `general.demo-directory` and the configured filename directly to `tv_record`. Automatic recordings use `general.default-file-name` for the `{fileName}` placeholder: with `"default-file-name": "retakes4"`, the default regular pattern produces `game/csgo/discord_demos/retakes4_<map>_<date>_<time>.dem` from the start. The `regular-file-naming-pattern` and `crop-rounds-file-naming-pattern` settings still control naming. An explicit name supplied through `tv_record <name>` overrides the default.

The plugin logs the requested and confirmed recording paths. It announces a start only after finding a non-empty file, retries failed starts, and clears the recording state on map changes and stops. After `tv_stoprecord`, it waits for a stable, accessible file before compressing it in place. Active recordings and pending uploads are excluded from cleanup. Demos shorter than `minimum-demo-duration` are not uploaded. The existing deletion settings still apply after processing/upload; set `delete-demo-after-upload` to `false` to keep the `.dem` locally.

The 2.1.3/2.1.4 temporary recordings named `k4gotv_<id>.dem` are not automatically renamed by 2.1.5: their original intended name was only held in memory. Preserve those files and move/rename them manually after stopping the server if needed.

To verify on a live server, join with the configured minimum player count, record for at least 10 seconds, then run `tv_stoprecord`. Check for `Demo recording confirmed` and `Demo finalized` in the logs, and verify playback of the `.dem` from the resulting archive. Repeat across a map change and with round cropping if enabled. Compilation and local file tests cannot validate playback in CS2.

Build with the .NET 10 SDK using `dotnet build src/K4-GOTV.csproj -c Release`. Run the standalone file-handling regression checks using `dotnet run --project tests/K4-GOTV.RegressionTests.csproj -c Release`.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- ROADMAP -->

## Roadmap

- [ ] No plans for now

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- LICENSE -->

## License

Distributed under the GPL-3.0 License. See `LICENSE.md` for more information.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

