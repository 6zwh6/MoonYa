# MoonYa —— 你的 AI 桌面工作伙伴

> 一款运行在 **Windows 桌面端**的 AI 生产力助手，将 AI 从"聊天框回答问题"升级为"直接操作电脑完成任务"。

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-blue)]()
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)]()
[![PHP](https://img.shields.io/badge/PHP-8.0%2B-777BB4)]()
[![Python](https://img.shields.io/badge/Python-3.11%2B-3776AB)]()
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

- **仓库地址**：`[https://github.com/6zwh6/MoonYa]`



---

## 📑 目录

- [1. 项目简介](#1-项目简介)
  - [1.1 想解决什么问题](#11-想解决什么问题)
  - [1.2 产品是什么](#12-产品是什么)
  - [1.3 目标用户与痛点](#13-目标用户与痛点)
  - [1.4 价值与意义](#14-价值与意义)
- [2. 功能特性](#2-功能特性)
- [3. 环境要求与依赖说明](#3-环境要求与依赖说明)
- [4. 本地安装与运行](#4-本地安装与运行)
- [5. 使用示例与核心代码片段](#5-使用示例与核心代码片段)
- [6. 配置说明](#6-配置说明)
- [7. 测试方法](#7-测试方法)
- [8. 贡献指南](#8-贡献指南)
- [9. 开源协议](#9-开源协议)
- [附录：目录结构](#附录目录结构)

---

## 1. 项目简介

**MoonYa** 是一款 Windows 桌面端 AI Agent，核心价值在于将 AI 从"回答者"变成"执行者"：用户通过自然语言或语音，即可让 AI 完成文件管理、代码编写与运行、软件安装、网页浏览、命令执行等桌面操作。基于 TRAE AI IDE 构建核心 Agent 调度能力，是国内较早落地 Windows 端 **Computer Use（桌面自动化）** 能力的桌面 AI Agent——内置 Python 3.11 运行环境，对接豆包、DeepSeek、GLM、Kimi、MiniMax 五款主流大模型，集键鼠模拟操作、浏览器自动化、代码命令执行、多模型与 Agent 调度于一体。

大多数 AI 助手"能说不能做"——只输出文字建议，无法实际操控电脑；传统 RPA 工具又高度依赖特定软件接口，换个应用就得重新适配。MoonYa 以大模型为决策核心，结合 Computer Use 能力，动态理解当前界面并推理下一步操作，真正做到"说一句话、操作任意软件"。

### 1.1 想解决什么问题

- **AI 能听不能做**：现有 AI 助手输出完文字就结束。MoonYa 通过 Computer Use 引擎对 UIA 元素进行精准点击、输入、拖拽，把"听懂"变成"做到"。
- **自动化工具僵化**：传统 RPA 录制固定流程，换一个界面就失效。MoonYa 以大模型为决策核心，动态理解当前界面并推理下一步操作，而非死板的回放脚本。
- **AI 写代码 ≠ AI 跑代码**：代码生成工具只给片段，环境搭建和调试仍靠手动。MoonYa 内置 Python 3.11 沙箱（CPU 30 秒 / 内存 512MB 限制），生成即运行、运行即反馈。
- **多模型工具各自为战**：五家厂商的数据和工具能力彼此割裂。MoonYa 统一接入并动态路由，多个 Agent 共享全部工具链，一次对话即可调度多模型协同。
- **代码运行门槛高**：AI 生成的代码需要用户自己装 Python、配环境、处理依赖。MoonYa 内置运行环境，开箱即用。

### 1.2 产品是什么

一款运行在 **Windows 桌面端**的 AI 生产力助手，将 AI 从"聊天框回答问题"升级为"直接操作电脑完成任务"。它不仅仅是"会聊天的机器人"，而是能替你动手的桌面工作伙伴：

- **全桌面操控**：基于 Windows UIA 自动化（UI 树最大深度 6 层、2000 元素）+ P/Invoke 键鼠模拟，AI 可直接操作任意桌面应用，不依赖特定软件接口。
- **代码命令即说即跑**：自然语言下达指令，MoonYa 自动在安全沙箱中执行 PowerShell / CMD / Python 脚本，内置 Python 3.11 无需用户配置环境，三级风险审核保障安全。
- **Web 运维自动化**：PuppeteerSharp 提供 20 多种浏览器操作原语（导航、点击、填表、DOM 解析），自动操作宝塔面板等 Web 管理后台。
- **多模型智能协同**：五大模型动态路由 + 多个专用 Agent（Code、Brain、Image、VLS、Video、Deep），`mode_gate` 按任务自动分配工具权限，一次对话联动多模型。
- **语音与文字双通道交互**：日常多轮对话 + 实时语音双向通话（可打断、可连续），屏幕水波光效实时反馈音量变化。
- **项目开发全流程闭环**："创建 Python Web 项目"→"解释这段代码"→"定位这个 bug"，一个窗口完成。

### 1.3 目标用户与痛点

| 用户群体 | 痛点与 MoonYa 的解法 |
|----------|----------------------|
| 👨‍💻 **程序员与开发者** | Code-Agent 生成代码，Brain-Agent 负责复杂推理与多步规划，命令在 Python 3.11 沙箱中直接执行，无需手动切换终端。 |
| 🖧 **个人站长与轻运维** | PuppeteerSharp 浏览器自动化操作宝塔面板等 Web 后台，完成站点创建、域名配置、SSL 证书部署等重复操作。 |
| 📂 **办公人群** | 自然语言即可对 50 多种文件类型进行读写整理，crawl4ai 引擎自动爬取网页生成摘要，告别浏览器和资源管理器之间频繁切换。 |
| 🎓 **学生群体** | Kimi 联网搜索 + Bing 搜索 + DeepSeek + GLM + 豆包四条管线并行，论文查阅、翻译、总结一站式完成；Image-Agent 与 MiniMax 辅助创意产出。 |
| 🎤 **不便打字 / 戴耳机的用户** | Push-to-Talk（Ctrl+Space 全局热键）与实时语音双向通话双模式切换，阿里云 Fun-ASR 转写 + MiniMax TTS 合成，不动键盘也能完整操控电脑。 |

### 1.4 价值与意义

**效率与落地价值**

- **全桌面覆盖**：Computer Use 键鼠模拟不依赖特定软件接口，理论上可操作一切桌面应用，突破传统自动化工具的边界。
- **零配置开箱即用**：内置 Python 3.11 运行环境，自然语言直接驱动命令与脚本执行，大幅降低技术使用门槛。
- **运维实战验证**：浏览器自动化已通过宝塔面板多轮稳定实测，可替代人工完成站点创建、域名绑定、证书部署等重复操作。

**技术与示范价值**

- **全栈独立开发**：由独立开发者基于 TRAE 独立完成，覆盖 C# WPF 桌面客户端、PHP 原生后端、Python 微服务全链路技术栈。
- **Computer Use 先行探索**：较早由个人开发者落地 Windows Computer Use 能力的项目，为桌面端 AI Agent 提供了轻量、可复制的参考方案。
- **AI Agent 生态创新实践**：展现了开发者借助 AI Agent 工具从零快速交付复杂项目的能力，也是桌面执行场景中的前沿探索。

### 核心亮点

- **零配置开箱即用**：内置 Python 3.11 运行环境与命令沙箱，自然语言即可驱动脚本执行，无需手动配环境。
- **全桌面操控**：基于 Windows UIA 自动化 + P/Invoke 键鼠模拟，理论上可操作一切桌面应用，不依赖特定软件接口。
- **多模型智能协同**：统一接入五大国产大模型并动态路由，多个专用 Agent 共享全部工具链，一次对话即可调度多模型。
- **语音与文字双通道**：Push-to-Talk（Ctrl+Space 全局热键）+ 全双工实时语音对话，不动键盘也能完整操控电脑。

> **开发者**：本项目由独立开发者基于 TRAE AI IDE 从零搭建，覆盖 C# WPF 桌面客户端、PHP 原生后端、Python 微服务全链路技术栈。

---

## 2. 功能特性

| 功能 | 说明 |
|------|------|
| 🖥️ **桌面自动化（Computer Use）** | AI 直接操控桌面：UIA 元素定位 + SendInput 键鼠模拟 + 屏幕截图视觉分析，三层降级算法保障稳定执行 |
| 🎙️ **语音交互** | Push-to-Talk（Ctrl+Space 全局热键）+ 全双工实时语音对话，阿里云 ASR + MiniMax TTS |
| 🤖 **多模型智能协同** | 同时接入 Kimi、DeepSeek、GLM、MiniMax、豆包五大国产大模型，智能路由按任务自动分配，一次对话联动多模型 |
| 🛠️ **全栈工具链** | 文件操作、命令沙箱执行、浏览器自动化（PuppeteerSharp）、代码生成、图片/视频生成、应用管理 |
| 🔍 **网页爬虫与搜索** | crawl4ai 爬虫引擎 + 三层搜索降级链（DuckDuckGo → Baidu → Bing） |
| 📁 **项目开发全流程闭环** | "创建项目"→"解释代码"→"定位 bug"一个窗口完成，命令在沙箱中直接执行 |
| 🧩 **专用 Agent 体系** | Code-Agent、Brain-Agent、Image-Agent、VLS-Agent、Video-Agent、Deep-Agent 按任务分工协作 |

### 技术架构

采用**端云协同三端分离架构**：

```
┌─────────────────────────────┐
│   C# .NET 8 WPF 桌面客户端   │  ← 用户交互 + 本地服务（文件/桌面/浏览器/沙箱）
├─────────────────────────────┤
│      PHP 8+ 原生后端         │  ← 对话路由、模型调度、工具分发、社区/管理后台
├─────────────────────────────┤
│   Python 3.11 微服务        │  ← 网页爬虫 + 搜索（aiohttp）
├─────────────────────────────┤
│   外部大模型 & 语音服务       │  ← Kimi / DeepSeek / GLM / MiniMax / 豆包 / 阿里云 ASR
└─────────────────────────────┘
```

本地服务端口分配：

| 端口 | 用途 | 所属服务 |
|------|------|----------|
| 58900 | 文件操作 / 桌面操控 / UIA API | FileOperationApiServer |
| 58901 | Python 爬虫 + 搜索后端 | MoonYa-Python (aiohttp) |
| 58903 | 命令 / Python 沙箱执行 | ExecutionApiServer |
| 58905 | 浏览器自动化（PuppeteerSharp） | BrowserApiServer |

---

## 3. 环境要求与依赖说明

### 3.1 环境要求

| 组件 | 最低要求 | 推荐 |
|------|----------|------|
| 操作系统 | Windows 10+ | Windows 11 |
| .NET SDK | .NET 8 SDK | .NET 8 SDK |
| PHP | 8.0+ | 8.2+ |
| Web 服务器 | 内置 `php -S` / Apache / Nginx | Apache 2.4+ |
| 数据库 | MySQL 5.7+ / MariaDB 10.4+ | MySQL 8.0 |
| Python | 3.10+ | 3.11+ |
| Node.js | 18+ | 20+（ASR WebSocket 代理，可选） |
| 显示器 | 1920×1080 | 1920×1080+（CU 模式） |

### 3.2 第三方依赖清单

**PHP 后端**（`MoonYa-Backend/composer.json`）

| 依赖 | 版本 | 用途 |
|------|------|------|
| phpmailer/phpmailer | ^6.9 | SMTP 邮件发送 |
| PHP 8+（原生） | - | HTTP 服务 + PDO 数据库 + cURL + GD |

**C# 桌面客户端**（`MoonYa.csproj`）

| 依赖 | 版本 | 用途 |
|------|------|------|
| CefSharp.Wpf.NETCore | 147.0.100 | 嵌入式 Chromium 浏览器渲染 |
| System.Speech | 10.0.9 | 本地 TTS 语音合成 |
| PuppeteerSharp | 20.0.0 | 浏览器自动化操控 |
| .NET 8 | net8.0-windows10.0.19041.0 | 桌面应用框架 |

**Python 微服务**（`MoonYa-Python/requirements.txt`）

| 依赖 | 版本 | 用途 |
|------|------|------|
| crawl4ai | >=0.8.0 | 网页爬虫引擎（Playwright 驱动） |
| aiohttp | >=3.11.0 | 异步 HTTP 服务框架 |
| jsbeautifier | >=1.15.0 | JS/CSS 代码格式化 |
| playwright | （运行时） | 浏览器内核（需 `playwright install chromium`） |

**前端 CDN**：highlight.js、mammoth（Word 预览）、pdf.js（PDF 预览）。

---

## 4. 本地安装与运行

> 下文以**完整桌面应用**为目标。开发调试也可仅启动 Web 后端（见方式二）。

### 步骤一：获取代码

```bash
git clone [请填写仓库地址] MoonYa
cd MoonYa
```

### 步骤二：安装各端依赖

**PHP 后端**

```bash
cd MoonYa-Backend
composer install
# 复制环境变量模板并填入 API Key（如不存在 .env.example，请手动创建 .env）
cp .env.example .env   # 若没有 .env.example，请参考第 6 节手动创建 .env
```

**Python 微服务**

```bash
cd MoonYa-Python
python -m venv .venv
.venv\Scripts\activate        # Windows
pip install -r requirements.txt
playwright install chromium    # 安装爬虫所需的 Chromium 内核
```

**C# 桌面客户端**

```bash
# 使用 Visual Studio 打开 MoonYa-Win/MoonYa-Solution/MoonYa-Solution.slnx（或 .sln）
# NuGet 会自动还原依赖；或改用 dotnet CLI：
cd MoonYa-Win/MoonYa-Solution/MoonYa
dotnet restore
dotnet build
```

**ASR WebSocket 代理（可选，用于语音识别）**

```bash
cd MoonYa-Backend/asr-ws-proxy
npm install
```

### 步骤三：准备数据库

1. 启动本地 MySQL / MariaDB 服务；
2. 创建数据库（默认 `ai_system`，字符集 `utf8mb4`）；
3. 导入表结构：

```bash
mysql -u <用户名> -p ai_system < MoonYa-Backend/sql/数据库.sql
```

### 步骤四：启动服务

**方式一：完整桌面应用（推荐）**

1. 启动 PHP 后端：`php -S 127.0.0.1:80 -t MoonYa-Backend/`
2. 启动 Python 微服务：`cd MoonYa-Python && python main.py`
3. 用 Visual Studio 打开 `MoonYa-Solution.slnx` 按 **F5** 运行客户端（或 `dotnet run`）
4. 客户端启动后会拉起本地后端并弹出 MoonYa 主窗口

**方式二：仅 Web 后端开发**

```bash
# 终端 1：Python 微服务
cd MoonYa-Python && python main.py

# 终端 2：PHP 后端
cd MoonYa-Backend && php -S 127.0.0.1:80

# 浏览器访问 http://127.0.0.1
```

**方式三：Docker 部署**

```bash
cd MoonYa-Win/MoonYa-Solution/MoonYa
docker-compose up -d
# 访问 http://localhost:8080
```

---

## 5. 使用示例与核心代码片段

### 5.1 自然语言驱动桌面操作（Computer Use）

在对话中输入目标，AI 会进入 Plan-Act-Verify 循环自动执行（UIA 优先 → 视觉降级 → 键盘策略）：

```
用户：帮我把桌面上的"周报.docx"重命名为"2026-07-20 周报.docx"，并用浏览器打开公司官网
MoonYa：
  [Plan] 1) 定位桌面文件  2) 重命名  3) 打开默认浏览器访问官网
  [Act]  find_element(桌面/周报.docx) → rename → open_app(browser) → navigate(官网)
  [Verify] 文件已重命名 ✓  浏览器已打开 ✓
```

### 5.2 命令沙箱执行（Python / PowerShell）

直接让 AI 运行脚本，结果在沙箱中反馈，内置风险分级审核：

```python
# 由 MoonYa 在 Python 3.11 沙箱中执行（CPU 30s / 内存 512MB 限制）
import os
print("当前目录文件：", os.listdir("."))
```

### 5.3 Python 微服务调用示例

```python
import aiohttp, asyncio

async def search(query: str):
    async with aiohttp.ClientSession() as s:
        async with s.post("http://127.0.0.1:58901/search",
                          json={"query": query}) as r:
            return await r.json()

async def crawl(url: str):
    async with aiohttp.ClientSession() as s:
        async with s.post("http://127.0.0.1:58901/crawl",
                          json={"url": url}) as r:
            return await r.text()   # SSE 流式返回

asyncio.run(search("MoonYa AI 桌面助手"))
```

### 5.4 C# 端键鼠操作（核心片段）

```csharp
// ComputerUseService.cs —— 通过 SendInput 完成绝对坐标点击
public void MouseClick(int x, int y, string button = "left", int click = 1)
{
    MoveToAbsolute(x, y);
    SendInput(MOUSEEVENTF.LEFTDOWN);
    SendInput(MOUSEEVENTF.LEFTUP);
}
```

> 完整能力见 `agent_config.php` 中定义的 34 个工具（文件、应用、命令、CU 键鼠、UIA、浏览器自动化等）。

---

## 6. 配置说明

### 6.1 环境变量（`.env`）

敏感凭证通过 `MoonYa-Backend/.env` 管理（已在 `.gitignore` 中排除，请勿提交）。

| 变量 | 说明 | 获取方式 |
|------|------|----------|
| `KIMI_API_KEY` | MoonShot Kimi API 密钥 | platform.moonshot.cn |
| `DEEPSEEK_API_KEY` | DeepSeek API 密钥 | platform.deepseek.com |
| `MINMAX_API_KEY` | MiniMax API 密钥 | platform.minimaxi.com |
| `GLM_API_KEY` | 智谱 GLM API 密钥 | open.bigmodel.cn |
| `ALIYUN_ASR_API_KEY` | 阿里云语音识别密钥 | aliyun.com |
| `YUNZHI_API_TOKEN` | 云知 API Token（OCR/天气/星座） | yunzhi.com |
| `SMTP_*` | QQ 邮箱 SMTP 配置 | mail.qq.com |
| `DB_*` | 数据库连接（host/user/pass/name） | 本地/远程 MySQL |
| `JWT_SECRET` | JWT 签名密钥 | 自定义随机字符串 |
| `ADMIN_SECRET` | 管理后台密钥 | 自定义 |

### 6.2 启动配置（`launcher_config.json`）

位于 C# 客户端目录，控制本地服务行为：

| 配置项 | 说明 |
|--------|------|
| `host` / `backend_url` | 后端服务地址 |
| `api_port` / `crawler_port` / `search_port` / `execution_port` | 各服务端口 |
| `file_operations.allowed_roots` | 允许的文件操作根路径白名单 |
| `file_operations.blocked_paths` | 禁止访问的系统路径 |
| `file_operations.max_file_size_mb` | 文件大小上限 |
| `execution_tools.risk_rules` | 命令风险分级规则（高/中/低） |
| `execution_tools.sandbox` | 沙箱资源限制（CPU/内存/超时） |
| `browser_automation` | 浏览器自动化配置（端口/超时/视口） |
| `app_install_sources` | 预配置的软件一键安装源 |

> **安全提示**：API Key 仅通过 `.env` 配置；生产环境建议使用更安全的密钥管理方案。

---

## 7. 测试方法

项目中已内置多类测试入口，覆盖后端与微服务，请在对应目录下运行。

### 7.1 PHP 后端测试

后端根目录包含大量 `test_*.php` 与冒烟测试脚本，例如：

```bash
cd MoonYa-Backend

# Computer Use / UIA 冒烟测试
php cu_smoke_test.php
php smoke_test_uia.php

# API 连通性测试
php test_api_simple.php
php test_server_connection.php
php test_register.php
```

> 其余可用测试：`test_all_search_models.php`、`test_kimi_search_verify.php`、`vls_test.php`、`test_prompt_compare.php` 等。

### 7.2 Python 微服务测试

```bash
cd MoonYa-Python
.venv\Scripts\activate

# 压力测试
python stress_test.py

# 功能测试
python test_function_calling.py
python test_temperature_limits.py
```

健康检查（服务启动后访问）：

```bash
curl http://127.0.0.1:58901/health
```

### 7.3 客户端（C#）验证

- 在 Visual Studio 中以 **Debug** 模式运行，观察输出窗口的本地服务（58900/58903/58905）启动日志；
- 通过主界面依次验证：多模型对话、文件操作、命令执行、浏览器自动化是否正常。

---

## 8. 贡献指南

欢迎参与 MoonYa 的开发！请遵循以下流程：

1. **Fork** 本仓库并克隆到本地；
2. 基于 `main`（或 `develop`）创建特性分支：`git checkout -b feat/your-feature`；
3. 保持代码风格一致，新增功能请同步补充对应测试；
4. 提交信息建议遵循 [Conventional Commits](https://www.conventionalcommits.org/)（如 `feat:` / `fix:` / `docs:`）；
5. 确保本地测试通过后再发起 **Pull Request**，并在 PR 中描述改动动机与验证方式；
6. 涉及 API Key、密钥等敏感信息时，请仅修改 `.env`，切勿提交明文凭证。

> 提交 Issue 或 PR 前，可先在 `[请填写讨论区/Issue 链接]` 中沟通设计思路，避免重复工作。

---

## 9. 开源协议

本项目基于 **Apache License 2.0** 开源，协议全文见仓库根目录 [`LICENSE`](LICENSE) 文件。

> Apache-2.0 允许自由使用、修改、分发，并附带明确的专利授权与贡献条款；分发修改版本时需保留版权与许可声明，并在修改文件上标注变更。详见 [choosealicense.com/licenses/apache-2.0](https://choosealicense.com/licenses/apache-2.0/)。

若需在源码文件中标注版权，可在文件头部添加：

```csharp
// Copyright 2026 MoonYa 版权所有者
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
```

---

## 附录：目录结构

```
MoonYa/
├── MoonYa-Backend/          # PHP 8+ 原生后端（核心业务、社区、管理后台）
│   ├── api.php              # 核心 API 分发器
│   ├── config.php           # 中央配置
│   ├── agent_config.php     # 34 个工具定义
│   ├── model_router.php     # 智能模型路由
│   ├── Services/            # AIAssistant（CU 主循环）等核心服务
│   ├── asr-ws-proxy/        # Node.js ASR WebSocket 代理
│   ├── sql/                 # 数据库表结构（35 张表）
│   └── composer.json
├── MoonYa-Win/              # C# .NET 8 WPF 桌面客户端
│   └── MoonYa-Solution/MoonYa/
│       ├── MoonYa.csproj
│       ├── Services/        # 18 个后台服务（CU/UIA/浏览器/沙箱等）
│       └── launcher_config.json
├── MoonYa-Python/           # Python 3.11 微服务
│   ├── main.py              # 统一服务入口（aiohttp, 58901）
│   ├── web_crawler/         # crawl4ai 爬虫引擎
│   ├── web_search/          # 三层搜索降级链
│   └── requirements.txt
├── MoonYa-Agent-Showcase/   # 产品展示宣传页
├── bin/                     # 编译输出（可直接运行）
├── CODE_WIKI.md             # 项目代码 Wiki（架构/依赖/运行指南）
└── README.md                # 本文件
```

---

<p align="center">
  MoonYa · 让 AI 从"回答者"变成"操作电脑者"
</p>
