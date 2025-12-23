# MS-PlayerModelChanger

## Commands
- !model `<index>` - change player model following index
- !modellist - print all available player model list in chat

## Requirements
- [ModSharp](https://github.com/Kxnrl/modsharp-public) v2.1.90 or newer
- [ClientPreferences](https://github.com/Kxnrl/modsharp-public)

## How to install 
Download latest version of module at [Releases Latest](https://github.com/Kianyaa/MS-PlayerModelChanger/releases/tag/Latest) and extract zip file and drop it in `game\sharp\modules` directory <br>
and add path of model in `model-list.json` follow JSON stucture below example

> [!NOTE]  
> `model-list.json.json` file will automatically create at `modules` directory after run plugin for first time

### Example JSON file `model-list.json`
```json
{
  "paths": [
    "characters/kianya/vrc/lime_obsidian/limeobsidian.vmdl",
    "characters/models/kianya/vrc/chiffon_marshmallow/chiffon_marshmallow.vmdl"
  ]
}
```
