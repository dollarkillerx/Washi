#!/usr/bin/env python3
"""Builds the Washi website into site/dist: English at the root, Japanese in ja/, Chinese in zh/.

    python3 site/build.py                     # site/dist, for https://washi-c0h.pages.dev
    python3 site/build.py --url https://…     # for another address (canonical and hreflang links)
    npx wrangler pages deploy site/dist --project-name washi --branch main

The page is one template below; every visible string is in T, in the three languages. Pictures in
site/img are taken by the app's screenshot test (tests/Washi.App.Tests, EditorTests.Screenshot) on a
made-up form, never a real one.
"""
import html, os, re, shutil, sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "dist")
REPO = "https://github.com/dollarkillerx/Washi"
SITE = "https://washi-c0h.pages.dev"  # the Cloudflare Pages project "washi"
LANGS = {"en": ("en", "", "English"), "ja": ("ja", "ja/", "日本語"), "zh": ("zh-Hans", "zh/", "中文")}

T = {
    "title": {
        "en": "Washi: flat PDF forms, made fillable. Open-source PDF toolkit",
        "ja": "Washi：紙の様式を、記入できる PDF に。オープンソースの PDF ツールキット",
        "zh": "Washi：把扁平的 PDF 表格变成可填写的表单。开源 PDF 工具包",
    },
    "description": {
        "en": "Open-source PDF toolkit for desktop, command line and AI agents. Finds the boxes on flat forms and makes them fillable; fills, signs, redacts, OCRs, merges and more. Built-in AI and an MCP server.",
        "ja": "デスクトップ・コマンドライン・AI エージェントのためのオープンソース PDF ツールキット。印刷された様式のマス目を見つけて記入できる PDF にし、入力・署名・墨消し・OCR・結合などもこなします。AI と MCP サーバーを内蔵。",
        "zh": "面向桌面、命令行和 AI 智能体的开源 PDF 工具包。自动找出扁平表格上的方框并做成可填写的表单，还能填写、签名、涂黑、OCR、合并等。内置 AI 和 MCP 服务器。",
    },
    "nav_features": {"en": "Features", "ja": "機能", "zh": "功能"},
    "nav_ai": {"en": "AI & MCP", "ja": "AI と MCP", "zh": "AI 与 MCP"},
    "nav_privacy": {"en": "Privacy", "ja": "プライバシー", "zh": "隐私"},
    "nav_get": {"en": "Get Washi", "ja": "入手する", "zh": "获取"},
    "eyebrow": {"en": "Open source · AGPL-3.0 · pre-alpha", "ja": "オープンソース · AGPL-3.0 · プレアルファ", "zh": "开源 · AGPL-3.0 · 预览阶段"},
    "h1": {"en": "Flat PDF forms,<br>made fillable.", "ja": "紙の様式を、<br>記入できる PDF に。", "zh": "扁平的 PDF 表格，<br>变成可填写的表单。"},
    "lead": {
        "en": "Washi finds the boxes on a printed form, turns them into real fields and fills them in. It also signs, redacts, edits, OCRs and organizes PDFs. One set of tools, three ways to use it: a desktop editor, a command line, and an MCP server for AI agents.",
        "ja": "Washi は印刷された様式のマス目を見つけ、本物の記入欄にして入力します。署名・墨消し・編集・OCR・ページ整理もできます。同じツールを、デスクトップのエディタ、コマンドライン、AI エージェント向けの MCP サーバーの 3 つの形で使えます。",
        "zh": "Washi 会找出打印表格上的方框，把它们变成真正的栏位并填写；也能签名、涂黑、编辑、OCR 和整理页面。同一套工具，三种用法：桌面编辑器、命令行，以及面向 AI 智能体的 MCP 服务器。",
    },
    "cta_github": {"en": "View on GitHub", "ja": "GitHub で見る", "zh": "在 GitHub 上查看"},
    "cta_build": {"en": "Build from source", "ja": "ソースからビルド", "zh": "从源码构建"},
    "shot_alt": {
        "en": "The Washi editor on a sample form: the detected fields are outlined in blue, the selected comb field in red, with its settings in the Field panel",
        "ja": "見本の様式を開いた Washi のエディタ。検出された記入欄が青、選択中のマス目が赤で示され、右の記入欄パネルに設定が表示されている",
        "zh": "在示例表格上打开的 Washi 编辑器：检测到的栏位以蓝色标出，选中的方格栏位为红色，右侧栏位面板显示其设置",
    },
    # Masume
    "m_kicker": {"en": "Masume engine", "ja": "Masume エンジン", "zh": "Masume 引擎"},
    "m_h2": {"en": "The boxes, found for you.", "ja": "マス目は、Washi が見つけます。", "zh": "方框，交给 Washi 来找。"},
    "m_intro": {
        "en": "Many PDF forms are flat: printed tables with nothing to click, sometimes with wrong data already printed in. Making them fillable by hand means drawing every field. Masume (マス目, the grid of boxes on a form) finds the cells, the comb boxes for digits and the □ checkboxes, and suggests a field for each. You check them, then save a fillable PDF.",
        "ja": "多くの PDF の様式は「平ら」です。表が印刷されているだけでクリックできる場所がなく、誤ったデータが印字済みのこともあります。手で記入可能にするには記入欄を一つずつ描くしかありません。Masume（マス目）は欄・数字用のマス・□ のチェック欄を見つけ、それぞれに記入欄を提案します。確認したら、記入できる PDF として保存します。",
        "zh": "很多 PDF 表格是“扁平”的：只是印好的表格，没有可点击的地方，有时还印着错误的数据。手工做成可填写的表单，意味着要逐个画出每个栏位。Masume（マス目，即表格上的方格）会找出单元格、填数字用的方格和 □ 复选框，并为每个都建议一个栏位。你确认后，保存为可填写的 PDF。",
    },
    "m_stat1": {"en": "precision on the 扶養控除 form", "ja": "扶養控除申告書での適合率", "zh": "扶養控除表上的精确率"},
    "m_stat2": {"en": "recall on the 保険料控除 form", "ja": "保険料控除申告書での再現率", "zh": "保険料控除表上的召回率"},
    "m_stat3": {"en": "seconds per form", "ja": "秒で 1 様式", "zh": "秒处理一份表格"},
    "m_table_caption": {
        "en": "Measured on the 国税庁 年末調整 forms, which ship with hand-made fields: <code>washi masume eval</code> ignores them, detects fields on the flat page, then compares.",
        "ja": "国税庁の年末調整の様式で測定。これらには手作りの記入欄が付いているので、<code>washi masume eval</code> はそれを無視して平らなページから検出し、比べます。",
        "zh": "在附带手工栏位的日本国税厅年末调整表格上测得：<code>washi masume eval</code> 先忽略这些栏位，在扁平页面上检测，再进行比较。",
    },
    "m_form": {"en": "form", "ja": "様式", "zh": "表格"},
    "m_real": {"en": "real fields", "ja": "手作りの欄", "zh": "实际栏位"},
    "m_found": {"en": "detected", "ja": "検出", "zh": "检测到"},
    "m_recall": {"en": "recall", "ja": "再現率", "zh": "召回率"},
    "m_precision": {"en": "precision", "ja": "適合率", "zh": "精确率"},
    "fields_alt": {
        "en": "Close-up of a sample form: Masume has outlined each box to fill in, including a comb field for a phone number",
        "ja": "見本の様式の拡大。記入するマスを Masume が一つずつ囲み、電話番号はマス目の欄になっている",
        "zh": "示例表格的局部放大：Masume 把每个要填的方框都框了出来，电话号码是方格栏位",
    },
    "redact_alt": {
        "en": "The Redact tool: a name is found on the page and marked to be removed from the file",
        "ja": "墨消しツール。ページ上の氏名が見つかり、ファイルから取り除く対象として示されている",
        "zh": "涂黑工具：在页面上找到一个姓名，并标记为要从文件中删除",
    },
    # three ways
    "w_kicker": {"en": "One toolkit, three ways in", "ja": "ひとつのツールキット、3 つの使い方", "zh": "一套工具，三种用法"},
    "w_h2": {"en": "For people, for scripts and for AI agents.", "ja": "人にも、スクリプトにも、AI エージェントにも。", "zh": "给人用，给脚本用，也给 AI 智能体用。"},
    "w_intro": {
        "en": "Every PDF operation is written once, as a tool. The desktop app, the command line, the built-in AI and the MCP server all call the same tools, so they behave the same everywhere.",
        "ja": "PDF の操作はすべて一度だけ「ツール」として書かれています。デスクトップアプリ、コマンドライン、内蔵 AI、MCP サーバーはどれも同じツールを呼ぶので、どこでも同じように動きます。",
        "zh": "每个 PDF 操作都只实现一次，作为一个“工具”。桌面应用、命令行、内置 AI 和 MCP 服务器调用的都是同一批工具，所以在哪里用效果都一样。",
    },
    "w1_h": {"en": "Desktop editor", "ja": "デスクトップエディタ", "zh": "桌面编辑器"},
    "w1_p": {
        "en": "Check the detected fields, fill and sign forms, redact, edit text and organize pages. Built with Avalonia; the interface speaks seven languages.",
        "ja": "検出された記入欄の確認、様式への入力と署名、墨消し、文字の編集、ページ整理。Avalonia 製で、画面は 7 言語に対応しています。",
        "zh": "检查检测到的栏位，填写并签署表格，涂黑、编辑文字、整理页面。基于 Avalonia 构建，界面支持七种语言。",
    },
    "w2_h": {"en": "Command line", "ja": "コマンドライン", "zh": "命令行"},
    "w2_p": {
        "en": "Every tool is a command, ready for scripts and batches: make a form fillable, OCR a scan, fill a copy per row of a CSV.",
        "ja": "すべてのツールがコマンドになっていて、スクリプトや一括処理に使えます。様式を記入可能にする、スキャンを OCR する、CSV の行ごとにコピーを作る、など。",
        "zh": "每个工具都是一条命令，方便脚本和批处理：把表格做成可填写、给扫描件 OCR、按 CSV 的每一行生成一份填好的副本。",
    },
    "w3_h": {"en": "MCP server for AI agents", "ja": "AI エージェント向け MCP サーバー", "zh": "面向 AI 智能体的 MCP 服务器"},
    "w3_p": {
        "en": "<code>washi-mcp</code> gives Claude Desktop, Claude Code and other MCP clients 47 PDF tools, only inside the folders you allow. In the app, every write waits for your approval.",
        "ja": "<code>washi-mcp</code> は Claude Desktop や Claude Code などの MCP クライアントに 47 の PDF ツールを提供します。使えるのは許可したフォルダの中だけ。アプリ経由では、書き込みのたびに承認を求めます。",
        "zh": "<code>washi-mcp</code> 为 Claude Desktop、Claude Code 等 MCP 客户端提供 47 个 PDF 工具，只能在你允许的文件夹内使用。通过应用连接时，每次写入都要先经你批准。",
    },
    # tools
    "t_kicker": {"en": "Features", "ja": "機能", "zh": "功能"},
    "t_h2": {"en": "Everything you do with a PDF, in six groups.", "ja": "PDF でやることを、6 つのグループに。", "zh": "处理 PDF 要做的事，分成六组。"},
    "t_intro": {
        "en": "The same six groups as the app's rail. Each tool saves a copy; the file you opened is never changed.",
        "ja": "アプリの左の列と同じ 6 つのグループです。どのツールもコピーを保存し、開いたファイルそのものは変えません。",
        "zh": "与应用左侧栏相同的六组。每个工具都保存为副本，不会改动你打开的原文件。",
    },
    "start_alt": {
        "en": "Washi's start page: every tool, grouped as Fill & sign, Edit, Pages, Review, Protect and Output",
        "ja": "Washi のスタートページ。すべてのツールが記入・署名、編集、ページ、レビュー、保護、仕上げに分かれて並ぶ",
        "zh": "Washi 的首页：所有工具按填写与签名、编辑、页面、审阅、保护、输出分组列出",
    },
    # AI
    "a_kicker": {"en": "Built-in AI", "ja": "内蔵 AI", "zh": "内置 AI"},
    "a_h2": {"en": "AI that works on your PDFs, and asks first.", "ja": "PDF を扱う AI。送る前に、必ず確認。", "zh": "会处理 PDF 的 AI，而且先征得你同意。"},
    "a_intro": {
        "en": "Bring your own model: Claude, GPT, DeepSeek, or a local model that never leaves your machine.",
        "ja": "使うモデルは自分で選べます。Claude、GPT、DeepSeek、または手元から出ないローカルモデル。",
        "zh": "模型由你选择：Claude、GPT、DeepSeek，或完全不离开本机的本地模型。",
    },
    "local": {"en": "Local (Ollama and other OpenAI-compatible servers)", "ja": "ローカル（Ollama などの OpenAI 互換サーバー）", "zh": "本地（Ollama 等 OpenAI 兼容服务器）"},
    "a1_b": {"en": "Names fields from the page image", "ja": "ページ画像から記入欄に名前を付ける", "zh": "根据页面图像给栏位命名"},
    "a1_s": {"en": "<code>p1_text_001</code> becomes <code>世帯主の氏名</code>, even when the PDF's own text is garbled.", "ja": "PDF の文字が化けていても、<code>p1_text_001</code> が <code>世帯主の氏名</code> になります。", "zh": "即使 PDF 自身的文字是乱码，<code>p1_text_001</code> 也能变成 <code>世帯主の氏名</code>。"},
    "a2_b": {"en": "Fills a form from your profile", "ja": "プロフィールから様式に入力", "zh": "根据你的资料填写表格"},
    "a2_s": {"en": "It fills only what your profile answers, and notes the rest. Long numbers are masked before anything is sent.", "ja": "プロフィールに答えがある欄だけを埋め、残りはメモで知らせます。長い番号は送る前に伏せます。", "zh": "只填写资料中有答案的栏位，其余的会给出说明。较长的号码在发送前会被遮蔽。"},
    "a3_b": {"en": "Answers questions about a document", "ja": "文書についての質問に答える", "zh": "回答关于文档的问题"},
    "a3_s": {"en": "Readable text goes as text; scans go as page images.", "ja": "読める文字はテキストで、スキャンはページ画像で渡します。", "zh": "可读的文字以文本发送，扫描件以页面图像发送。"},
    "a4_b": {"en": "An assistant with the same tools as the MCP server", "ja": "MCP サーバーと同じツールを使うアシスタント", "zh": "与 MCP 服务器使用相同工具的助手"},
    "a4_s": {"en": "Ask it to make a form fillable, fill it, merge or split files. Text inside a PDF cannot steer it, and every write shows a card to allow or deny.", "ja": "様式を記入可能にする、入力する、ファイルを結合・分割するなどを頼めます。PDF の中の文字で操られることはなく、書き込みのたびに許可・拒否のカードが出ます。", "zh": "可以让它把表格做成可填写、填写表格、合并或拆分文件。PDF 里的文字无法操控它，每次写入都会弹出“允许 / 拒绝”的卡片。"},
    "mcp_title": {"en": "Connect Claude Code to Washi", "ja": "Claude Code を Washi につなぐ", "zh": "把 Claude Code 连接到 Washi"},
    "mcp_c1": {"en": "# standalone: stdio, only inside the folder you allow", "ja": "# 単体で: stdio、許可したフォルダの中だけ", "zh": "# 独立运行：stdio，只在允许的文件夹内"},
    "mcp_c2": {"en": "# or the desktop app's server (Settings → AI & MCP shows the exact command)", "ja": "# またはデスクトップアプリのサーバー（設定 → AI と MCP に正確なコマンドが出ます）", "zh": "# 或使用桌面应用的服务器（设置 → AI 与 MCP 中会显示完整命令）"},
    # privacy
    "p_kicker": {"en": "Privacy by design", "ja": "設計からのプライバシー", "zh": "隐私从设计开始"},
    "p_h2": {"en": "Your forms hold your life. Washi treats them that way.", "ja": "様式には暮らしの情報が詰まっています。Washi はそのように扱います。", "zh": "表格里装着你的生活信息，Washi 会郑重对待。"},
    "p1_b": {"en": "Consent first", "ja": "まず同意", "zh": "先征得同意"},
    "p1_s": {"en": "Before anything goes to a cloud model, Washi says what will be sent and waits for your yes.", "ja": "クラウドのモデルに何かを送る前に、何を送るかを示して同意を待ちます。", "zh": "在向云端模型发送任何内容之前，Washi 会说明要发送什么，并等你同意。"},
    "p2_b": {"en": "Long numbers never leave", "ja": "長い番号は外に出さない", "zh": "长号码不出本机"},
    "p2_s": {"en": "マイナンバー, account, card and phone numbers become placeholders. The model only learns “a 12-digit number”; the digits are put back on your machine.", "ja": "マイナンバー・口座・カード・電話番号は置き換えて送ります。モデルが知るのは「12 桁の数字」ということだけで、数字は手元で戻します。", "zh": "个人编号、账号、卡号和电话号码会被替换成占位符。模型只知道“一个 12 位数字”，真实数字在你的电脑上再填回去。"},
    "p3_b": {"en": "Local models stay local", "ja": "ローカルモデルは手元のまま", "zh": "本地模型留在本地"},
    "p3_s": {"en": "With a model on your own machine nothing leaves it, and nothing is asked.", "ja": "自分のマシンのモデルなら何も外に出ず、確認も不要です。", "zh": "使用本机上的模型时，什么都不会离开电脑，也无需确认。"},
    "p4_b": {"en": "Copies, in folders you choose", "ja": "コピーを、選んだフォルダに", "zh": "只写副本，只在你选的文件夹"},
    "p4_s": {"en": "Tools write new files. AI agents work only inside folders you allow, and the in-app MCP server is off by default and listens on this computer only.", "ja": "ツールは新しいファイルに書き出します。AI エージェントは許可したフォルダの中だけで動き、アプリ内の MCP サーバーは初期状態でオフ、このコンピュータからの接続しか受けません。", "zh": "工具写出的是新文件。AI 智能体只能在你允许的文件夹内操作；应用内置的 MCP 服务器默认关闭，且只监听本机。"},
    "p5_b": {"en": "Redaction that really removes", "ja": "本当に消す墨消し", "zh": "真正删除的涂黑"},
    "p5_s": {"en": "Text, image pixels and drawings under a black box are taken out of the file, not just covered, and the copy is checked afterwards.", "ja": "黒塗りの下の文字・画像の画素・図形はファイルから取り除かれ、上から隠すだけではありません。保存後にコピーを確認します。", "zh": "黑框下的文字、图像像素和图形会从文件中删除，而不只是被盖住；保存后还会检查副本。"},
    "p6_b": {"en": "Keys in the Keychain", "ja": "キーはキーチェーンに", "zh": "密钥存放在钥匙串"},
    "p6_s": {"en": "API keys come from the macOS Keychain or the environment, never from a settings file.", "ja": "API キーは macOS のキーチェーンか環境変数から読み、設定ファイルには置きません。", "zh": "API 密钥来自 macOS 钥匙串或环境变量，从不写进设置文件。"},
    # CLI
    "c_kicker": {"en": "Command line", "ja": "コマンドライン", "zh": "命令行"},
    "c_h2": {"en": "Scriptable from the first command.", "ja": "最初のコマンドから、スクリプトに。", "zh": "从第一条命令起就能写进脚本。"},
    "c1": {"en": "# detect the fields on a flat form and write them", "ja": "# 平らな様式の記入欄を検出して書き込む", "zh": "# 检测扁平表格上的栏位并写入"},
    "c2": {"en": "# let the AI name the fields, then fill from a profile", "ja": "# AI に記入欄の名前を付けさせ、プロフィールから入力", "zh": "# 让 AI 给栏位命名，再根据资料填写"},
    "c3": {"en": "# one filled copy per row of a table", "ja": "# 表の 1 行ごとに入力済みのコピーを作る", "zh": "# 表格的每一行生成一份填好的副本"},
    "c4": {"en": "# make a scan searchable, then redact a name and phone numbers", "ja": "# スキャンを検索可能にし、氏名と電話番号を墨消し", "zh": "# 让扫描件可搜索，再涂黑姓名和电话号码"},
    # get
    "g_kicker": {"en": "Get Washi", "ja": "入手する", "zh": "获取 Washi"},
    "g_h2": {"en": "Free and open source.", "ja": "無料、そしてオープンソース。", "zh": "免费，并且开源。"},
    "g_intro": {
        "en": "Washi is pre-alpha: there are no installers yet. It builds from source with the .NET 10 SDK.",
        "ja": "Washi はプレアルファ版で、まだインストーラーはありません。.NET 10 SDK でソースからビルドできます。",
        "zh": "Washi 目前处于预览阶段，还没有安装包。可以用 .NET 10 SDK 从源码构建。",
    },
    "g1": {"en": "Install the <a href=\"https://dotnet.microsoft.com/download\">.NET 10 SDK</a>.", "ja": "<a href=\"https://dotnet.microsoft.com/download\">.NET 10 SDK</a> をインストールします。", "zh": "安装 <a href=\"https://dotnet.microsoft.com/download\">.NET 10 SDK</a>。"},
    "g2": {"en": "Clone the repository and run the desktop app, or the command line.", "ja": "リポジトリをクローンし、デスクトップアプリかコマンドラインを起動します。", "zh": "克隆仓库，然后运行桌面应用或命令行。"},
    "g3": {"en": "On a Mac, <code>build/macos/bundle.sh</code> makes <code>Washi.app</code>.", "ja": "Mac では <code>build/macos/bundle.sh</code> で <code>Washi.app</code> を作れます。", "zh": "在 Mac 上，<code>build/macos/bundle.sh</code> 会生成 <code>Washi.app</code>。"},
    "g_license": {
        "en": "Licensed under the GNU AGPL v3.0 or later. Use it, change it, share it; if you run a changed version as a network service, publish its source too.",
        "ja": "ライセンスは GNU AGPL v3.0 以降です。使う・変える・配ることは自由です。変更した版をネットワークサービスとして動かす場合は、そのソースも公開してください。",
        "zh": "采用 GNU AGPL v3.0 或更高版本授权。可以自由使用、修改和分发；如果把修改后的版本作为网络服务运行，也需要公开其源码。",
    },
    "g_langs": {"en": "The app speaks", "ja": "アプリの表示言語", "zh": "应用支持的语言"},
    "footer": {
        "en": "Washi (和紙) is an open-source PDF toolkit. The mark is a sheet of paper with a grid of form boxes; the vermilion box is the field being filled in.",
        "ja": "Washi（和紙）はオープンソースの PDF ツールキットです。マークは記入欄のマス目が並んだ一枚の紙。朱色のマスは、いま記入している欄です。",
        "zh": "Washi（和纸）是一个开源 PDF 工具包。标志是一张印有表格方格的纸，朱红色的方格代表正在填写的栏位。",
    },
    "f_source": {"en": "Source code", "ja": "ソースコード", "zh": "源代码"},
    "f_issues": {"en": "Issues", "ja": "不具合・要望", "zh": "问题反馈"},
    "f_license": {"en": "License", "ja": "ライセンス", "zh": "许可证"},
}

# the six groups, in the app's order: title, icon (the app's own 20×20 paths), tools
GROUPS = [
    ({"en": "Fill & sign", "ja": "記入・署名", "zh": "填写与签名"},
     "M3,2 L17,2 L17,18 L3,18 Z M6,6 L14,6 L14,9 L6,9 Z M6,12 L9,12 L9,15 L6,15 Z M11,12 L14,12 L14,15 L11,15 Z",
     [{"en": "Make flat forms fillable: text, comb boxes, checkboxes", "ja": "平らな様式を記入可能に：文字・マス目・チェック欄", "zh": "把扁平表格做成可填写：文本、方格、复选框"},
      {"en": "Fill existing forms in place, even ones locked against changes that allow filling in", "ja": "既存のフォームにその場で入力（変更は禁止でも入力は許可されたものも）", "zh": "直接填写现有表单，包括禁止修改但允许填写的"},
      {"en": "Formats, ranges and totals worked out as you type", "ja": "表示形式・範囲・合計を入力に合わせて計算", "zh": "格式、范围和合计随输入自动计算"},
      {"en": "Form data in and out: FDF, XFDF, CSV, JSON; a copy per row", "ja": "フォームデータの入出力：FDF・XFDF・CSV・JSON、行ごとのコピー", "zh": "表单数据导入导出：FDF、XFDF、CSV、JSON；按行生成副本"},
      {"en": "Ink signatures, and digital signatures (PAdES, long-term validation, certification)", "ja": "手書き署名と電子署名（PAdES・長期検証・認証署名）", "zh": "手写签名与数字签名（PAdES、长期验证、认证签名）"}]),
    ({"en": "Edit", "ja": "編集", "zh": "编辑"},
     "M3,4 L12,4 M7.5,4 L7.5,16 M15.5,3 L15.5,17 M13.5,3 L17.5,3 M13.5,17 L17.5,17",
     [{"en": "Edit printed text: a line, a paragraph, or find and replace", "ja": "印刷された文字を編集：1 行・段落・検索と置換", "zh": "编辑已印文字：单行、整段，或查找替换"},
      {"en": "Replace, add and remove images", "ja": "画像の差し替え・追加・削除", "zh": "替换、添加和删除图片"},
      {"en": "Links: add, change, or link every address in the text", "ja": "リンクの追加・変更、本文中のアドレスを一括リンク", "zh": "链接：添加、修改，或为文中所有地址加链接"},
      {"en": "Headers, footers, page and Bates numbers, watermarks", "ja": "ヘッダー・フッター・ページ番号・ベイツ番号・透かし", "zh": "页眉、页脚、页码、Bates 编号、水印"},
      {"en": "Erase what is already printed on a page, for real", "ja": "ページに印字済みの内容を本当に消去", "zh": "真正删除页面上已印好的内容"}]),
    ({"en": "Pages", "ja": "ページ", "zh": "页面"},
     "M3,3 L9,3 L9,9 L3,9 Z M11,3 L17,3 L17,9 L11,9 Z M3,11 L9,11 L9,17 L3,17 Z M11,11 L17,11 L17,17 L11,17 Z",
     [{"en": "Reorder, rotate, delete and insert pages", "ja": "ページの並べ替え・回転・削除・挿入", "zh": "重排、旋转、删除和插入页面"},
      {"en": "Combine PDFs and photos into one file, form fields kept", "ja": "PDF や写真を 1 つに結合（記入欄はそのまま）", "zh": "把 PDF 和照片合并成一个文件，保留表单栏位"},
      {"en": "Split by page ranges", "ja": "ページ範囲で分割", "zh": "按页码范围拆分"},
      {"en": "Crop, and save pages as images", "ja": "切り抜き、ページを画像として保存", "zh": "裁剪页面，或把页面存为图片"}]),
    ({"en": "Review", "ja": "レビュー", "zh": "审阅"},
     "M3,3 L17,3 L17,13 L9,13 L5,17 L5,13 L3,13 Z M6,7 L14,7 M6,10 L11,10",
     [{"en": "Comments and replies, and editing existing ones", "ja": "注釈と返信、既存の注釈の編集", "zh": "注释与回复，以及编辑已有注释"},
      {"en": "Compare two versions: words, pages and pictures that changed", "ja": "2 つの版を比較：変わった語句・ページ・図", "zh": "比较两个版本：变化的文字、页面和图片"},
      {"en": "Find in the document, bookmarks", "ja": "文書内の検索、しおり", "zh": "文档内查找、书签"},
      {"en": "Print", "ja": "印刷", "zh": "打印"}]),
    ({"en": "Protect", "ja": "保護", "zh": "保护"},
     "M4.5,9 L15.5,9 L15.5,18 L4.5,18 Z M6.5,9 L6.5,6.5 C6.5,1.8 13.5,1.8 13.5,6.5 L13.5,9 M10,12 L10,15",
     [{"en": "Passwords and permissions (AES-256)", "ja": "パスワードと権限（AES-256）", "zh": "密码与权限（AES-256）"},
      {"en": "Encrypt for certificates: opens only with the recipients' digital IDs", "ja": "証明書で暗号化：受取人の電子 ID でしか開けない", "zh": "证书加密：只有收件人的数字 ID 才能打开"},
      {"en": "Redact by word, pattern (e-mail, phone, ID numbers) or area", "ja": "語句・パターン（メール・電話・番号）・範囲で墨消し", "zh": "按文字、模式（邮箱、电话、证件号）或区域涂黑"},
      {"en": "Remove hidden information: metadata, scripts, earlier versions", "ja": "隠れた情報の削除：メタデータ・スクリプト・以前の版", "zh": "删除隐藏信息：元数据、脚本、旧版本"}]),
    ({"en": "Output", "ja": "仕上げ", "zh": "输出"},
     "M3,4 L17,4 L17,7 L3,7 Z M4.5,7 L4.5,16 L15.5,16 L15.5,7 M8,10 L12,10",
     [{"en": "Compress", "ja": "圧縮", "zh": "压缩"},
      {"en": "OCR: Apple Vision, Tesseract or an AI model", "ja": "OCR：Apple Vision・Tesseract・AI モデル", "zh": "OCR：Apple Vision、Tesseract 或 AI 模型"},
      {"en": "Flatten fields and comments; check and convert to PDF/A-2b", "ja": "記入欄と注釈の確定、PDF/A-2b の検査と変換", "zh": "拼合栏位和注释；检查并转换为 PDF/A-2b"},
      {"en": "Page labels, attachments and layers", "ja": "ページラベル・添付ファイル・レイヤー", "zh": "页码标签、附件和图层"}]),
]

SCORES = [("扶養控除等(異動)申告書 R9", 193, 130, 78, 100), ("保険料控除申告書 R8", 143, 135, 84, 87),
          ("基礎・配偶者・特定親族・所得金額調整 R8", 83, 78, 83, 88)]

ICON = {
    "desktop": "M2,3 L18,3 L18,14 L2,14 Z M7,17 L13,17 M10,14 L10,17 M5,6 L9,6 M5,9 L12,9",
    "cli": "M2,3 L18,3 L18,17 L2,17 Z M5,8 L8,10.5 L5,13 M10,13 L14,13",
    "mcp": "M10,2 L11.6,8.4 L18,10 L11.6,11.6 L10,18 L8.4,11.6 L2,10 L8.4,8.4 Z",
}
GITHUB = ('<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 '
          '0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 '
          '1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 '
          '1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 '
          '1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/></svg>')


def svg(path, cls=""):
    return f'<svg viewBox="0 0 20 20" aria-hidden="true"{cls}><path d="{path}"/></svg>'


def page(lang, base_url):
    code, sub, _ = LANGS[lang]
    root = "../" if sub else ""
    t = lambda k: T[k][lang]
    esc = html.escape

    current = ' aria-current="page"'
    langs = "".join(
        f'<a href="{root}{s}" hreflang="{c}" lang="{c}"{current if k == lang else ""}>{n}</a>'
        for k, (c, s, n) in LANGS.items())
    alternates = ""
    if base_url:
        alternates = "".join(f'<link rel="alternate" hreflang="{c}" href="{base_url}/{s}">\n' for c, s, _ in LANGS.values())
        alternates += f'<link rel="alternate" hreflang="x-default" href="{base_url}/">\n'
        alternates += f'<link rel="canonical" href="{base_url}/{sub}">\n'
        alternates += f'<meta property="og:url" content="{base_url}/{sub}">\n'
        alternates += f'<meta property="og:image" content="{base_url}/img/social-preview.png">\n'

    groups = "".join(
        f'<div class="group"><h3>{svg(icon)}{title[lang]}</h3><ul>' + "".join(f"<li>{i[lang]}</li>" for i in items) + "</ul></div>"
        for title, icon, items in GROUPS)
    scores = "".join(
        f'<tr><td>{f}</td><td class="n">{real}</td><td class="n">{found}</td><td class="n">{r}%</td><td class="n">{p}%</td></tr>'
        for f, real, found, r, p in SCORES)
    providers = "".join(f"<li>{p}</li>" for p in ["Claude", "GPT", "DeepSeek", t("local")])
    checks = lambda keys: "".join(f"<li><b>{t(k + '_b')}</b><span>{t(k + '_s')}</span></li>" for k in keys)

    return f"""<!doctype html>
<html lang="{code}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{esc(t("title"))}</title>
<meta name="description" content="{esc(t("description"))}">
<meta property="og:type" content="website">
<meta property="og:title" content="{esc(t("title"))}">
<meta property="og:description" content="{esc(t("description"))}">
<meta name="twitter:card" content="summary_large_image">
<meta name="theme-color" content="#161616">
{alternates}<link rel="icon" href="{root}img/favicon.ico" sizes="any">
<link rel="icon" href="{root}img/logo.svg" type="image/svg+xml">
<link rel="apple-touch-icon" href="{root}img/apple-touch-icon.png">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400&family=IBM+Plex+Sans+JP:wght@400;500;600&family=IBM+Plex+Sans:wght@300;400;500;600&display=swap">
<link rel="stylesheet" href="{root}style.css">
</head>
<body>
<header class="top">
  <div class="wrap">
    <a class="brand" href="{root}{sub}"><img src="{root}img/logo.svg" alt="" width="26" height="26">Washi <small>和紙</small></a>
    <nav class="nav" aria-label="Main">
      <a class="sec" href="#features">{t("nav_features")}</a>
      <a class="sec" href="#ai">{t("nav_ai")}</a>
      <a class="sec" href="#privacy">{t("nav_privacy")}</a>
      <a class="sec" href="#get">{t("nav_get")}</a>
      <a class="gh" href="{REPO}">GitHub</a>
      <span class="lang">{langs}</span>
    </nav>
  </div>
</header>

<main>
<section class="hero" style="border:0;padding:0">
  <div class="wrap">
    <span class="eyebrow"><i></i>{t("eyebrow")}</span>
    <h1>{t("h1")}</h1>
    <p class="lead">{t("lead")}</p>
    <div class="cta">
      <a class="btn primary" href="{REPO}">{GITHUB}{t("cta_github")}</a>
      <a class="btn ghost" href="#get">{t("cta_build")}</a>
    </div>
    <figure class="shot" style="margin-bottom:0">
      <img src="{root}img/editor-{lang}.png" width="1440" height="900" alt="{esc(t("shot_alt"))}" fetchpriority="high">
    </figure>
  </div>
</section>

<section id="masume">
  <div class="wrap">
    <p class="kicker">{t("m_kicker")}</p>
    <h2>{t("m_h2")}</h2>
    <p class="intro">{t("m_intro")}</p>
    <div class="split">
      <div>
        <div class="stats">
          <div class="stat"><b>100%</b><span>{t("m_stat1")}</span></div>
          <div class="stat"><b>84%</b><span>{t("m_stat2")}</span></div>
          <div class="stat"><b>1–2</b><span>{t("m_stat3")}</span></div>
        </div>
        <div class="table-scroll">
        <table class="score">
          <thead><tr><th>{t("m_form")}</th><th class="n">{t("m_real")}</th><th class="n">{t("m_found")}</th><th class="n">{t("m_recall")}</th><th class="n">{t("m_precision")}</th></tr></thead>
          <tbody>{scores}</tbody>
        </table>
        </div>
        <p class="caption" style="margin-top:12px">{t("m_table_caption")}</p>
      </div>
      <figure class="framed" style="margin:0">
        <img src="{root}img/fields.png" width="560" height="460" loading="lazy" alt="{esc(t("fields_alt"))}">
      </figure>
    </div>
  </div>
</section>

<section class="alt">
  <div class="wrap">
    <p class="kicker">{t("w_kicker")}</p>
    <h2>{t("w_h2")}</h2>
    <p class="intro">{t("w_intro")}</p>
    <div class="tiles three">
      <div class="tile"><div class="icon">{svg(ICON["desktop"])}</div><h3>{t("w1_h")}</h3><p>{t("w1_p")}</p></div>
      <div class="tile"><div class="icon">{svg(ICON["cli"])}</div><h3>{t("w2_h")}</h3><p>{t("w2_p")}</p><code>washi form make form.pdf</code></div>
      <div class="tile"><div class="icon">{svg(ICON["mcp"])}</div><h3>{t("w3_h")}</h3><p>{t("w3_p")}</p></div>
    </div>
  </div>
</section>

<section id="features">
  <div class="wrap">
    <p class="kicker">{t("t_kicker")}</p>
    <h2>{t("t_h2")}</h2>
    <p class="intro">{t("t_intro")}</p>
    <div class="groups">{groups}</div>
    <figure class="framed" style="margin:48px auto 0;max-width:960px">
      <img src="{root}img/start-{lang}.png" width="960" height="720" loading="lazy" alt="{esc(t("start_alt"))}">
    </figure>
  </div>
</section>

<section id="ai" class="alt">
  <div class="wrap">
    <p class="kicker">{t("a_kicker")}</p>
    <h2>{t("a_h2")}</h2>
    <p class="intro">{t("a_intro")}</p>
    <ul class="providers">{providers}</ul>
    <div class="split" style="align-items:start">
      <ul class="checks" style="margin:0">{checks(["a1", "a2", "a3", "a4"])}</ul>
      <div>
        <div class="code-title">{t("mcp_title")}</div>
<pre class="code"><span class="c">{t("mcp_c1")}</span>
<span class="p">$ </span>claude mcp add washi -- washi-mcp --allow ~/Documents/forms

<span class="c">{t("mcp_c2")}</span>
<span class="p">$ </span>claude mcp add --transport http washi http://127.0.0.1:PORT/mcp \\
    --header "Authorization: Bearer TOKEN"</pre>
      </div>
    </div>
  </div>
</section>

<section id="privacy">
  <div class="wrap">
    <p class="kicker">{t("p_kicker")}</p>
    <h2>{t("p_h2")}</h2>
    <div class="tiles three">
      {"".join(f'<div class="tile"><h3>{t(k + "_b")}</h3><p>{t(k + "_s")}</p></div>' for k in ["p1", "p2", "p3", "p4", "p5", "p6"])}
    </div>
    <figure class="framed" style="margin:48px 0 0">
      <img src="{root}img/redact-{lang}.png" width="1440" height="900" loading="lazy" alt="{esc(t("redact_alt"))}">
    </figure>
  </div>
</section>

<section class="alt">
  <div class="wrap">
    <p class="kicker">{t("c_kicker")}</p>
    <h2>{t("c_h2")}</h2>
<pre class="code" style="margin-top:40px"><span class="c">{t("c1")}</span>
<span class="p">$ </span>washi form make form.pdf -o form_fillable.pdf

<span class="c">{t("c2")}</span>
<span class="p">$ </span>washi ai name-fields form.pdf -o form_fields.json
<span class="p">$ </span>washi form make form.pdf --fields form_fields.json
<span class="p">$ </span>washi ai fill form_fillable.pdf --profile me.txt

<span class="c">{t("c3")}</span>
<span class="p">$ </span>washi pdf data form.pdf --rows staff.csv --name-column 社員番号

<span class="c">{t("c4")}</span>
<span class="p">$ </span>washi ocr scan.pdf --lang ja,en
<span class="p">$ </span>washi pdf redact scan_ocr.pdf --find "山田太郎" --pattern phone</pre>
  </div>
</section>

<section id="get">
  <div class="wrap">
    <p class="kicker">{t("g_kicker")}</p>
    <h2>{t("g_h2")}</h2>
    <p class="intro">{t("g_intro")}</p>
    <div class="split" style="align-items:start">
      <div>
        <ol class="steps">
          <li>{t("g1")}</li>
          <li>{t("g2")}</li>
          <li>{t("g3")}</li>
        </ol>
        <p class="note">{t("g_license")}</p>
        <p style="margin-top:24px;color:var(--muted)">{t("g_langs")}</p>
        <div class="langs"><span>English</span><span>日本語</span><span>简体中文</span><span>繁體中文</span><span>한국어</span><span>Français</span><span>Deutsch</span></div>
      </div>
<pre class="code"><span class="p">$ </span>git clone {REPO}.git
<span class="p">$ </span>cd Washi
<span class="p">$ </span>dotnet run --project src/Washi.App
<span class="p">$ </span>dotnet run --project src/Washi.Cli -- --version</pre>
    </div>
  </div>
</section>
</main>

<footer>
  <div class="wrap">
    <div>
      <a class="brand" href="{root}{sub}"><img src="{root}img/logo.svg" alt="" width="26" height="26">Washi <small>和紙</small></a>
      <p>{t("footer")}</p>
    </div>
    <div style="display:flex;gap:20px;flex-wrap:wrap">
      <a href="{REPO}">{t("f_source")}</a>
      <a href="{REPO}/issues">{t("f_issues")}</a>
      <a href="{REPO}/blob/main/LICENSE">{t("f_license")} · AGPL-3.0</a>
    </div>
  </div>
</footer>
</body>
</html>
"""


def main():
    base_url = SITE
    if "--url" in sys.argv:
        base_url = sys.argv[sys.argv.index("--url") + 1].rstrip("/")
    for k, v in T.items():
        assert set(v) == set(LANGS), k
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(OUT)
    shutil.copy(os.path.join(HERE, "style.css"), OUT)
    shutil.copytree(os.path.join(HERE, "img"), os.path.join(OUT, "img"))
    for lang, (_, sub, _) in LANGS.items():
        d = os.path.join(OUT, sub)
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "index.html"), "w", encoding="utf-8") as f:
            f.write(page(lang, base_url))
    with open(os.path.join(OUT, "_headers"), "w") as f:
        f.write("/img/*\n  Cache-Control: public, max-age=604800\n/*\n  X-Content-Type-Options: nosniff\n  Referrer-Policy: strict-origin-when-cross-origin\n")
    with open(os.path.join(OUT, "robots.txt"), "w") as f:
        f.write("User-agent: *\nAllow: /\n")
    print("wrote", OUT)


if __name__ == "__main__":
    main()
