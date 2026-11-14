# SMD 动画拼接工具

一个图形界面小工具（单文件 exe），包含两个功能页签：

- **动画拼接（三文件）**：把「主骨骼 / 机件动画 / 身体动画」三个 SMD 合成一个动画文件。
- **手臂重定向**：把一套骨架的手臂动作转到另一套骨骼上。

## 下载（Windows，免编译）

| 程序 | 说明 |
| --- | --- |
| [smd_merge.exe](https://github.com/MinGUWU0139/smd-merge/raw/main/bin/smd_merge.exe) | 图形界面版，含「动画拼接」和「手臂重定向」两个页签 |

> 仓库是私有的，下载需要登录你的 GitHub 账号；想要公开直链可以把仓库改成 Public。

## 功能

- **三文件拼接**：主骨骼决定骨骼表，身体动画提供身体骨骼，机件动画提供枪械机件（滑套、弹匣、击锤等）
- **放大倍数**：位置坐标整体缩放，旋转不变
- **手臂重定向**：把一套骨架的手臂动作转到另一套骨骼上，目标模型静止姿势不变
- 拖拽文件到窗口自动分类；回车直接开始；记住上次的路径

## 目录

```
src/
  smd_merge_gui.cs     图形界面（拼接 + 重定向，两个页签）
  smd_merge.cs         控制台版（仅拼接）
  sleeve_retarget.cs   手臂动作重定向（供界面调用）
bin/
  smd_merge.exe        编译好的图形界面版
build.bat              编译
run.bat                运行
注意事项与声明.txt
```

## 编译

用 Windows 自带的 .NET Framework 编译器，不需要装 SDK：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:anycpu /optimize+ /out:smd_merge.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\smd_merge_gui.cs src\sleeve_retarget.cs
```

## 用法

### 页签 1：动画拼接

依次选「主骨骼」「机件动画」「身体动画」→ 填输出路径 → 放大倍数（1 = 不放大）→ 开始拼接。

命令行：

```bat
smd_merge.exe --cli <主骨骼.smd> <机件动画.smd> <身体动画.smd> [输出.smd] [--scale N]
```

### 页签 2：手臂重定向

选「动作源」（含手臂动画的 SMD）和「目标骨架」（静止姿势 SMD）→ 填输出 → 开始重定向。

命令行：

```bat
smd_merge.exe --retarget <动作源.smd> <目标骨架静止.smd> [输出.smd]
```

## 合并规则

- 骨骼表（节点名、父子关系）用主骨骼文件的
- 身体动画里有的骨骼，逐帧取身体动画
- 机件骨骼（slide / clip / hammer / silencer / trigger）逐帧取机件动画
- 其余骨骼保留主骨骼静止值（weapon / weapon_offset 必须保留：机件动画里的 weapon 是根节点，按名覆盖会把枪翻转）
- 帧数 = min(机件帧数, 身体帧数)
- `--scale N` 只缩放位置坐标，旋转不变

## 手臂重定向

在各自的静止姿势坐标系里套增量：`D = 源静止⁻¹ × 当前`，`目标世界 = 目标静止 × D`，再换算回局部。骨骼位置沿用目标骨架的静止偏移，第 0 帧与目标静止姿势一致。

## 坐标约定

SMD 的欧拉角按 `R = Rz(rz) · Ry(ry) · Rx(rx)` 解释（Blender `XYZ` 导出）。
