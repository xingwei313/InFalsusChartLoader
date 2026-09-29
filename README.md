# InFalsusChartLoader

In Falsus的自制谱加载Mod，使用[MelonLoader](https://github.com/LavaGang/MelonLoader)加载

---

## 构建

需要 [.NET SDK](https://dotnet.microsoft.com/download)（目标框架 `net6.0`）

```cmd
build.bat          :: Release构建
build.bat Debug    :: Debug构建
```

Release构建产物位于 `bin/Release/InFalsusChartLoader.dll`。

Debug构建产物位于 `bin/Debug/InFalsusChartLoader.dll`。

---

## 安装

1. 安装 [MelonLoader](https://github.com/LavaGang/MelonLoader)
2. 获取 `InFalsusChartLoader.dll`
3. 放进游戏目录下的 `Mods` 文件夹
4. 启动游戏

自制谱放在 `Charts` 文件夹（即 `GAME_PATH/Charts`，若不存在则会自动创建）

---

## 使用

`Charts` 下**每个子文件夹即为一首歌**，mod会严格按照引导文件 `if` 进行加载

`if` 文件示例：
```json
{
  "id": "20260929052150xingwei",              //谱面id，需要尽可能的保证它是唯一的，若重复则不加载谱面
  "name": "music",                            //曲名
  "composer": "I don’t know",                 //曲师
  "song": "music.mp3",                        //歌曲文件，接受ogg、mp3、wav
  "illust": "I don’t know",                   //曲绘画师
  "picture": "bg.png",                        //曲绘，只接受png，需要为正方形
  //"picture": ["bg.png", "gb.png", "gg.png", "bb.png"],
  //也可以为每张谱面单独设置曲绘[Minimal,Evolved,Ultimate,Forbidden]
  "preview_seconds": ["0", "60"],
  //预览音频时间[开始,结束]，以秒计算
  "Charter": ["Skytarry", "MagicNeko", "XingWei", "Xing_W"],
  //谱师[Minimal,Evolved,Ultimate,Forbidden]
  "chart": ["temp.spc", "dummy.spc", "untitled.spc", "chart.spc"],
  //谱面文件，不兼容demo格式[Minimal,Evolved,Ultimate,Forbidden]
  "lv": [1, 1, 1, 15]
  //谱面难度[Minimal,Evolved,Ultimate,Forbidden]
}
```
文件夹示例：
```
Charts
      `-- chart
          |-- bb.png
          |-- bg.png
          |-- chart.spc
          |-- dummy.spc
          |-- gb.png
          |-- gg.png
          |-- if
          |-- music.mp3
          |-- temp.spc
          `-- untitled.spc
```

加载后，选曲界面最上方会出现合集 **IFCL**

if文件只要有一条信息非法则不会导入该谱面

需要查看完整日志时请使用**Debug构建**

---

## 致谢

- [MelonLoader](https://github.com/LavaGang/MelonLoader) —— mod 加载器

## 免责声明

本仓库为非官方第三方项目，与 In Falsus、lowiro limited 及其关联公司、子公司、许可方或其他相关实体均无任何隶属、合作、赞助、认可、授权或其他关联关系，如有侵权请联系作者删除

## 真-致谢

- deepseek v4.1 flash —— 除了整体思路、部分流程与部分逆向工程以外的全部操作