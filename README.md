# CHIPLOOP

CHIPLOOP 是一个原生 Windows 8bit 音乐制作器。每小节 16 步，可以编排旋律、低音和鼓点，调节速度，循环试听，然后导出整首 WAV。

## 下载安装

1. 打开 [GitHub 最新发布版](https://github.com/llig9578/chiploop/releases/latest)。
2. 展开页面下方的 **Assets（附件）**，下载 [CHIPLOOP.exe](https://github.com/llig9578/chiploop/releases/latest/download/CHIPLOOP.exe)。
3. 将 EXE 保存到你打算长期存放它的文件夹，双击运行。程序无需安装，可离线使用，需要 Windows 上的 .NET Framework 4.x。

## 使用

用「总小节」设置 1–32 小节，再用「编辑小节」切换要编写的小节。每小节有四拍，界面会按 BPM 显示整首时长。切换音轨后，左键添加音符，右键擦除音符。按住拖动可连续添加或擦除。「清空本小节音轨」只清空当前小节选中的音轨。临时缩短时，超出范围的小节会保留在工程里；增加小节数可找回这些音符。

「播放」会循环播放所有选定小节。「导出 WAV」会保存完整的一轮，格式为 44.1 kHz、16-bit、单声道 PCM。

用「保存工程」或 Ctrl+S 将音符、BPM 和小节数保存到 .chiploop 文件。首次运行新版 EXE 后，可在资源管理器里双击 .chiploop 文件直接打开。也可以用「打开工程」或 Ctrl+O。移动 EXE 后，请从新位置运行新版一次，以更新关联。关闭程序或打开另一份工程前，如果有未保存的修改，程序会提示保存。

## 从源码构建

在包含 `Studio.cs` 的目录打开 PowerShell，然后运行：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /out:CHIPLOOP.exe Studio.cs
```
