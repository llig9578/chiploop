# CHIPLOOP

CHIPLOOP 是一个原生 Windows 8bit 音乐制作器。用 16 步音序器编排旋律、低音和鼓点，调节速度，循环试听，然后导出一轮 WAV。

## 使用

从 GitHub Releases 下载 `CHIPLOOP.exe`，双击打开。程序使用 Windows Forms，可离线运行，需要 Windows 上的 .NET Framework 4.x。

切换音轨后点击格子添加或移除音符。点击「播放」试听。使用「清空当前轨」从空白开始，或点击「加载示例」恢复示例。「导出 WAV」会保存当前四拍循环，格式为 44.1 kHz、16-bit、单声道 PCM。

## 从源码构建

在包含 `Studio.cs` 的目录打开 PowerShell，然后运行：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Accessibility.dll /out:CHIPLOOP.exe Studio.cs
```
