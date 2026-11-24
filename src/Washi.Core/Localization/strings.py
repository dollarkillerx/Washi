# SPDX-License-Identifier: AGPL-3.0-or-later
"""Source of truth for UI strings. Run `python3 strings.py` to regenerate the .resx files.

Every language must have exactly the same keys and the same {n} placeholders
(tests/Washi.Masume.Tests/LocalizationTests.cs checks this).
"""
import os
from xml.sax.saxutils import escape

LANGS = ["en", "ja", "zh-Hans", "zh-Hant", "ko", "fr", "de"]
FORM_LANGS = "auto|ja|zh-Hans|zh-Hant|ko|en|fr|de"

S = {
    "Usage": {
        "en": f"""washi {{0}} - open-source PDF toolkit (pre-alpha)

usage:
  washi --version
  washi masume detect <in.pdf> [-o out]   suggest form fields -> fields.json + overlay PNGs
  washi masume eval   <in.pdf> [-o out]   score detection against the PDF's existing fields
  washi form make     <in.pdf> [-o out.pdf] [--fields fields.json] [--lang {FORM_LANGS}]
                                          make a fillable PDF (fields are detected unless --fields is given)
                                          --embed-font <font.ttf|.otf|.ttc> [--font-index n]: embed a whole font
                                          so rare characters show in every viewer (adds the font size to the file)

The interface follows your system language. Set WASHI_LANG (e.g. ja, ko, fr) to change it.""",
        "ja": f"""washi {{0}} - オープンソースの PDF ツールキット（プレアルファ版）

使い方:
  washi --version
  washi masume detect <in.pdf> [-o out]   記入欄を検出 -> fields.json と確認用 PNG
  washi masume eval   <in.pdf> [-o out]   PDF にある既存の記入欄と比べて検出結果を採点
  washi form make     <in.pdf> [-o out.pdf] [--fields fields.json] [--lang {FORM_LANGS}]
                                          記入できる PDF を作成（--fields がなければ記入欄を自動検出）
                                          --embed-font <font.ttf|.otf|.ttc> [--font-index n]: フォント全体を埋め込み、
                                          どのビューアでも難しい漢字まで表示（ファイルはフォントの分だけ大きくなります）

表示言語はシステムの設定に従います。変更するには WASHI_LANG（例: ja, ko, fr）を設定してください。""",
        "zh-Hans": f"""washi {{0}} - 开源 PDF 工具（预览版）

用法:
  washi --version
  washi masume detect <in.pdf> [-o out]   识别填写栏 -> fields.json 和标注图 PNG
  washi masume eval   <in.pdf> [-o out]   与 PDF 里已有的填写栏对比，为识别结果打分
  washi form make     <in.pdf> [-o out.pdf] [--fields fields.json] [--lang {FORM_LANGS}]
                                          生成可填写的 PDF（不指定 --fields 时自动识别填写栏）
                                          --embed-font <font.ttf|.otf|.ttc> [--font-index n]：嵌入完整字体，
                                          让生僻字在任何阅读器里都能显示（文件会增加字体大小）

界面语言跟随系统设置。要更改，请设置 WASHI_LANG（例如 zh-Hans、ja、ko）。""",
        "zh-Hant": f"""washi {{0}} - 開源 PDF 工具（預覽版）

用法:
  washi --version
  washi masume detect <in.pdf> [-o out]   辨識填寫欄 -> fields.json 和標註圖 PNG
  washi masume eval   <in.pdf> [-o out]   與 PDF 中既有的填寫欄比對，為辨識結果評分
  washi form make     <in.pdf> [-o out.pdf] [--fields fields.json] [--lang {FORM_LANGS}]
                                          產生可填寫的 PDF（未指定 --fields 時自動辨識填寫欄）
                                          --embed-font <font.ttf|.otf|.ttc> [--font-index n]：內嵌完整字型，
                                          讓罕用字在任何閱讀器中都能顯示（檔案會增加字型大小）

介面語言依照系統設定。若要變更，請設定 WASHI_LANG（例如 zh-Hant、ja、ko）。""",
        "ko": f"""washi {{0}} - 오픈 소스 PDF 툴킷 (프리알파)

사용법:
  washi --version
  washi masume detect <in.pdf> [-o out]   입력란 감지 -> fields.json 과 확인용 PNG
  washi masume eval   <in.pdf> [-o out]   PDF 에 있는 기존 입력란과 비교해 감지 결과 채점
  washi form make     <in.pdf> [-o out.pdf] [--fields fields.json] [--lang {FORM_LANGS}]
                                          작성 가능한 PDF 생성 (--fields 가 없으면 입력란을 자동 감지)
                                          --embed-font <font.ttf|.otf|.ttc> [--font-index n]: 글꼴 전체를 포함해
                                          어떤 뷰어에서도 드문 한자까지 표시 (파일이 글꼴 크기만큼 커집니다)

인터페이스 언어는 시스템 설정을 따릅니다. 바꾸려면 WASHI_LANG (예: ko, ja, fr) 을 설정하세요.""",
        "fr": f"""washi {{0}} - boîte à outils PDF open source (pré-alpha)

utilisation :
  washi --version
  washi masume detect <in.pdf> [-o out]   détecte les champs -> fields.json + images PNG de contrôle
  washi masume eval   <in.pdf> [-o out]   note la détection par rapport aux champs déjà présents dans le PDF
  washi form make     <in.pdf> [-o out.pdf] [--fields fields.json] [--lang {FORM_LANGS}]
                                          crée un PDF remplissable (champs détectés si --fields est absent)
                                          --embed-font <police.ttf|.otf|.ttc> [--font-index n] : incorpore une police entière
                                          pour que les caractères rares s’affichent partout (le fichier grossit d’autant)

L’interface suit la langue du système. Définissez WASHI_LANG (p. ex. fr, de, ja) pour la changer.""",
        "de": f"""washi {{0}} - Open-Source-PDF-Werkzeugkasten (Pre-Alpha)

Verwendung:
  washi --version
  washi masume detect <in.pdf> [-o out]   Formularfelder erkennen -> fields.json + PNG-Kontrollbilder
  washi masume eval   <in.pdf> [-o out]   Erkennung mit den vorhandenen Feldern des PDFs vergleichen
  washi form make     <in.pdf> [-o out.pdf] [--fields fields.json] [--lang {FORM_LANGS}]
                                          ausfüllbares PDF erstellen (ohne --fields werden Felder erkannt)
                                          --embed-font <schrift.ttf|.otf|.ttc> [--font-index n]: bettet eine ganze Schrift ein,
                                          damit seltene Zeichen überall erscheinen (die Datei wird um die Schrift größer)

Die Oberfläche folgt der Systemsprache. Mit WASHI_LANG (z. B. de, fr, ja) lässt sie sich ändern.""",
    },
    "DetectSummary": {
        "en": "{0}: {1} fields ({2}) -> {3}/  [{4} s]",
        "ja": "{0}: 記入欄 {1} 個（{2}） -> {3}/  [{4} 秒]",
        "zh-Hans": "{0}: {1} 个填写栏（{2}） -> {3}/  [{4} 秒]",
        "zh-Hant": "{0}: {1} 個填寫欄（{2}） -> {3}/  [{4} 秒]",
        "ko": "{0}: 입력란 {1}개 ({2}) -> {3}/  [{4}초]",
        "fr": "{0} : {1} champs ({2}) -> {3}/  [{4} s]",
        "de": "{0}: {1} Felder ({2}) -> {3}/  [{4} s]",
    },
    "PrefilledNote": {
        "en": "  {0} fields already contain printed data (red dashed outline in the overlay)",
        "ja": "  {0} 個の記入欄にはすでに文字が印刷されています（確認用画像の赤い点線）",
        "zh-Hans": "  {0} 个填写栏里已经印有内容（标注图中的红色虚线）",
        "zh-Hant": "  {0} 個填寫欄中已印有內容（標註圖中的紅色虛線）",
        "ko": "  입력란 {0}개에 이미 인쇄된 내용이 있습니다 (확인용 이미지의 빨간 점선)",
        "fr": "  {0} champs contiennent déjà du texte imprimé (pointillés rouges sur l’image de contrôle)",
        "de": "  {0} Felder enthalten bereits gedruckten Text (rot gestrichelt im Kontrollbild)",
    },
    "EvalSummary": {
        "en": "{0}: real fields {1}, detected {2} ({3})  [{4} s]",
        "ja": "{0}: 既存の記入欄 {1} 個、検出 {2} 個（{3}）  [{4} 秒]",
        "zh-Hans": "{0}: 原有填写栏 {1} 个，识别出 {2} 个（{3}）  [{4} 秒]",
        "zh-Hant": "{0}: 原有填寫欄 {1} 個，辨識出 {2} 個（{3}）  [{4} 秒]",
        "ko": "{0}: 기존 입력란 {1}개, 감지 {2}개 ({3})  [{4}초]",
        "fr": "{0} : {1} champs réels, {2} détectés ({3})  [{4} s]",
        "de": "{0}: {1} echte Felder, {2} erkannt ({3})  [{4} s]",
    },
    "EvalRecall": {
        "en": "  recall    {0}/{1} = {2:P0}   (real fields covered by a detection)",
        "ja": "  再現率    {0}/{1} = {2:P0}   （検出でカバーされた既存の記入欄）",
        "zh-Hans": "  召回率    {0}/{1} = {2:P0}   （被识别结果覆盖的原有填写栏）",
        "zh-Hant": "  召回率    {0}/{1} = {2:P0}   （被辨識結果涵蓋的原有填寫欄）",
        "ko": "  재현율    {0}/{1} = {2:P0}   (감지 결과가 덮은 기존 입력란)",
        "fr": "  rappel    {0}/{1} = {2:P0}   (champs réels couverts par une détection)",
        "de": "  Recall    {0}/{1} = {2:P0}   (echte Felder, die eine Erkennung abdeckt)",
    },
    "EvalPrecision": {
        "en": "  precision {0}/{1} = {2:P0}   (detections that sit on a real field)",
        "ja": "  適合率    {0}/{1} = {2:P0}   （既存の記入欄に重なる検出）",
        "zh-Hans": "  准确率    {0}/{1} = {2:P0}   （落在原有填写栏上的识别结果）",
        "zh-Hant": "  準確率    {0}/{1} = {2:P0}   （落在原有填寫欄上的辨識結果）",
        "ko": "  정밀도    {0}/{1} = {2:P0}   (기존 입력란 위에 있는 감지 결과)",
        "fr": "  précision {0}/{1} = {2:P0}   (détections situées sur un champ réel)",
        "de": "  Precision {0}/{1} = {2:P0}   (Erkennungen, die auf einem echten Feld liegen)",
    },
    "FormMade": {
        "en": "{0}: {1} fields -> {2}  [form language: {3}, {4}]",
        "ja": "{0}: 記入欄 {1} 個 -> {2}  [フォームの言語: {3}、{4}]",
        "zh-Hans": "{0}: {1} 个填写栏 -> {2}  [表单语言：{3}，{4}]",
        "zh-Hant": "{0}: {1} 個填寫欄 -> {2}  [表單語言：{3}，{4}]",
        "ko": "{0}: 입력란 {1}개 -> {2}  [양식 언어: {3}, {4}]",
        "fr": "{0} : {1} champs -> {2}  [langue du formulaire : {3}, {4}]",
        "de": "{0}: {1} Felder -> {2}  [Formularsprache: {3}, {4}]",
    },
    "LangFromOption": {
        "en": "set with --lang", "ja": "--lang で指定", "zh-Hans": "由 --lang 指定", "zh-Hant": "由 --lang 指定",
        "ko": "--lang 으로 지정", "fr": "choisie avec --lang", "de": "per --lang gesetzt",
    },
    "LangFromText": {
        "en": "detected from the page text", "ja": "ページの文字から判定", "zh-Hans": "根据页面文字判断",
        "zh-Hant": "根據頁面文字判斷", "ko": "페이지 텍스트로 판단", "fr": "déduite du texte de la page",
        "de": "aus dem Seitentext erkannt",
    },
    "LangFromSystem": {
        "en": "from your system language (the page text was unreadable)",
        "ja": "システムの言語から（ページの文字を読み取れませんでした）",
        "zh-Hans": "按系统语言（页面文字无法读取）",
        "zh-Hant": "依系統語言（無法讀取頁面文字）",
        "ko": "시스템 언어 기준 (페이지 텍스트를 읽을 수 없음)",
        "fr": "d’après la langue du système (texte de la page illisible)",
        "de": "nach der Systemsprache (Seitentext nicht lesbar)",
    },
    "PagesStraightened": {
        "en": "  {0} rotated page(s) straightened so the fields stay upright",
        "ja": "  回転していた {0} ページを、記入欄が正しい向きになるよう補正しました",
        "zh-Hans": "  已把 {0} 个旋转过的页面摆正，让填写栏方向正确",
        "zh-Hant": "  已將 {0} 個旋轉過的頁面轉正，讓填寫欄方向正確",
        "ko": "  회전된 페이지 {0}개를 입력란이 바로 보이도록 바로잡았습니다",
        "fr": "  {0} page(s) pivotée(s) redressée(s) pour que les champs restent droits",
        "de": "  {0} gedrehte Seite(n) aufgerichtet, damit die Felder gerade stehen",
    },
    "Warning": {
        "en": "  warning: {0}", "ja": "  注意: {0}", "zh-Hans": "  注意：{0}", "zh-Hant": "  注意：{0}",
        "ko": "  주의: {0}", "fr": "  attention : {0}", "de": "  Hinweis: {0}",
    },
    "OpenFailed": {
        "en": "Could not open {0}: {1}",
        "ja": "{0} を開けませんでした: {1}",
        "zh-Hans": "无法打开 {0}：{1}",
        "zh-Hant": "無法開啟 {0}：{1}",
        "ko": "{0} 을(를) 열 수 없습니다: {1}",
        "fr": "Impossible d’ouvrir {0} : {1}",
        "de": "{0} konnte nicht geöffnet werden: {1}",
    },
    "ProtectedPdf": {
        "en": "{0} is protected: changes need its permissions password. Enter it in Protect to save an unprotected copy, or ask the author for one.",
        "ja": "{0} は保護されています。変更には権限パスワードが必要です。「保護」で入力すると保護のないコピーを保存できます。わからない場合は作成者に依頼してください。",
        "zh-Hans": "{0} 有保护：修改需要权限密码。在“保护”中输入后可另存无保护的副本，或向作者索取。",
        "zh-Hant": "{0} 有保護：修改需要權限密碼。在「保護」中輸入後可另存無保護的副本，或向作者索取。",
        "ko": "{0} 은(는) 보호되어 있습니다. 변경하려면 권한 암호가 필요합니다. '보호'에서 입력하면 보호 없는 사본을 저장할 수 있으며, 모르면 작성자에게 요청하세요.",
        "fr": "{0} est protégé : les modifications exigent son mot de passe d’autorisations. Saisissez-le dans Protéger pour enregistrer une copie non protégée, ou demandez-en une à l’auteur.",
        "de": "{0} ist geschützt: Änderungen brauchen das Berechtigungspasswort. Geben Sie es unter Schützen ein, um eine ungeschützte Kopie zu speichern, oder bitten Sie den Ersteller darum.",
    },
    "FontEmbedded": {
        "en": "  embedded font {0} ({1} MB): every viewer can show all of its characters",
        "ja": "  フォント {0} を埋め込みました（{1} MB）。どのビューアでもこのフォントの全文字を表示できます",
        "zh-Hans": "  已嵌入字体 {0}（{1} MB），任何阅读器都能显示其中所有字符",
        "zh-Hant": "  已內嵌字型 {0}（{1} MB），任何閱讀器都能顯示其中所有字元",
        "ko": "  글꼴 {0} 을(를) 포함했습니다 ({1} MB). 어떤 뷰어에서도 이 글꼴의 모든 문자를 표시할 수 있습니다",
        "fr": "  police {0} incorporée ({1} Mo) : tous les lecteurs peuvent afficher tous ses caractères",
        "de": "  Schrift {0} eingebettet ({1} MB): jeder Viewer kann alle ihre Zeichen anzeigen",
    },
    "FontNotAllowed": {
        "en": "The licence of {0} does not allow embedding it in a PDF people can edit (OS/2 fsType {1}). Choose another font.",
        "ja": "{0} のライセンスでは、編集できる PDF への埋め込みが許可されていません（OS/2 fsType {1}）。別のフォントを選んでください。",
        "zh-Hans": "{0} 的授权不允许嵌入到可编辑的 PDF 中（OS/2 fsType {1}）。请换一个字体。",
        "zh-Hant": "{0} 的授權不允許內嵌到可編輯的 PDF 中（OS/2 fsType {1}）。請改用其他字型。",
        "ko": "{0} 의 라이선스는 편집 가능한 PDF 에 포함하는 것을 허용하지 않습니다 (OS/2 fsType {1}). 다른 글꼴을 선택하세요.",
        "fr": "La licence de {0} interdit de l’incorporer dans un PDF modifiable (OS/2 fsType {1}). Choisissez une autre police.",
        "de": "Die Lizenz von {0} erlaubt kein Einbetten in ein bearbeitbares PDF (OS/2 fsType {1}). Bitte eine andere Schrift wählen.",
    },
    "FontNotSupported": {
        "en": "{0} cannot be embedded: variable fonts and files that are not TrueType/OpenType fonts are not supported. Use a static .ttf, .otf or .ttc.",
        "ja": "{0} は埋め込めません。可変フォントや TrueType/OpenType 以外のファイルには対応していません。固定ウェイトの .ttf / .otf / .ttc を使ってください。",
        "zh-Hans": "无法嵌入 {0}：不支持可变字体，也不支持 TrueType/OpenType 以外的文件。请使用固定字重的 .ttf、.otf 或 .ttc。",
        "zh-Hant": "無法內嵌 {0}：不支援可變字型，也不支援 TrueType/OpenType 以外的檔案。請使用固定字重的 .ttf、.otf 或 .ttc。",
        "ko": "{0} 을(를) 포함할 수 없습니다. 가변 글꼴과 TrueType/OpenType 이 아닌 파일은 지원하지 않습니다. 고정 굵기의 .ttf, .otf, .ttc 를 사용하세요.",
        "fr": "Impossible d’incorporer {0} : les polices variables et les fichiers qui ne sont pas des polices TrueType/OpenType ne sont pas pris en charge. Utilisez un .ttf, .otf ou .ttc statique.",
        "de": "{0} kann nicht eingebettet werden: variable Schriften und Dateien, die keine TrueType/OpenType-Schriften sind, werden nicht unterstützt. Bitte eine statische .ttf, .otf oder .ttc verwenden.",
    },
    "UnknownLanguage": {
        "en": f"Unknown form language '{{0}}'. Use one of: {FORM_LANGS.replace('|', ', ')}",
        "ja": f"フォームの言語 '{{0}}' は使えません。次のいずれかを指定してください: {FORM_LANGS.replace('|', ', ')}",
        "zh-Hans": f"不支持的表单语言“{{0}}”。请使用：{FORM_LANGS.replace('|', '、')}",
        "zh-Hant": f"不支援的表單語言「{{0}}」。請使用：{FORM_LANGS.replace('|', '、')}",
        "ko": f"양식 언어 '{{0}}' 은(는) 지원되지 않습니다. 다음 중 하나를 사용하세요: {FORM_LANGS.replace('|', ', ')}",
        "fr": f"Langue de formulaire inconnue « {{0}} ». Valeurs possibles : {FORM_LANGS.replace('|', ', ')}",
        "de": f"Unbekannte Formularsprache „{{0}}“. Möglich sind: {FORM_LANGS.replace('|', ', ')}",
    },
    "Ui_Open": {"en": "Open PDF…", "ja": "PDF を開く…", "zh-Hans": "打开 PDF…", "zh-Hant": "開啟 PDF…", "ko": "PDF 열기…", "fr": "Ouvrir un PDF…", "de": "PDF öffnen…"},
    "Ui_Detect": {"en": "Detect fields", "ja": "記入欄を検出", "zh-Hans": "识别填写栏", "zh-Hant": "辨識填寫欄", "ko": "입력란 감지", "fr": "Détecter les champs", "de": "Felder erkennen"},
    "Ui_Draw": {"en": "Draw field", "ja": "記入欄を描く", "zh-Hans": "画填写栏", "zh-Hant": "繪製填寫欄", "ko": "입력란 그리기", "fr": "Dessiner un champ", "de": "Feld zeichnen"},
    "Ui_Delete": {"en": "Delete", "ja": "削除", "zh-Hans": "删除", "zh-Hant": "刪除", "ko": "삭제", "fr": "Supprimer", "de": "Löschen"},
    "Ui_LoadFields": {"en": "Load fields.json…", "ja": "fields.json を読み込む…", "zh-Hans": "载入 fields.json…", "zh-Hant": "載入 fields.json…", "ko": "fields.json 불러오기…", "fr": "Charger fields.json…", "de": "fields.json laden…"},
    "Ui_SaveFields": {"en": "Save fields.json…", "ja": "fields.json を保存…", "zh-Hans": "保存 fields.json…", "zh-Hant": "儲存 fields.json…", "ko": "fields.json 저장…", "fr": "Enregistrer fields.json…", "de": "fields.json speichern…"},
    "Ui_MakeFillable": {"en": "Make fillable PDF…", "ja": "記入できる PDF を作成…", "zh-Hans": "生成可填写 PDF…", "zh-Hant": "產生可填寫 PDF…", "ko": "작성 가능한 PDF 만들기…", "fr": "Créer le PDF remplissable…", "de": "Ausfüllbares PDF erstellen…"},
    "Ui_FormLanguage": {"en": "Form language", "ja": "フォームの言語", "zh-Hans": "表单语言", "zh-Hant": "表單語言", "ko": "양식 언어", "fr": "Langue du formulaire", "de": "Formularsprache"},
    "Ui_LangAuto": {"en": "Automatic", "ja": "自動", "zh-Hans": "自动", "zh-Hant": "自動", "ko": "자동", "fr": "Automatique", "de": "Automatisch"},
    "Ui_EmbedFont": {"en": "Embed font…", "ja": "フォントを埋め込む…", "zh-Hans": "嵌入字体…", "zh-Hant": "內嵌字型…", "ko": "글꼴 포함…", "fr": "Incorporer une police…", "de": "Schrift einbetten…"},
    "Ui_NoFont": {"en": "No embedded font", "ja": "埋め込みなし", "zh-Hans": "不嵌入字体", "zh-Hant": "不內嵌字型", "ko": "글꼴 포함 안 함", "fr": "Aucune police incorporée", "de": "Keine eingebettete Schrift"},
    "Ui_Zoom": {"en": "Zoom", "ja": "表示倍率", "zh-Hans": "缩放", "zh-Hant": "縮放", "ko": "확대/축소", "fr": "Zoom", "de": "Zoom"},
    "Ui_PageOf": {"en": "Page {0} / {1}", "ja": "{0} / {1} ページ", "zh-Hans": "第 {0} / {1} 页", "zh-Hant": "第 {0} / {1} 頁", "ko": "{0} / {1} 페이지", "fr": "Page {0} / {1}", "de": "Seite {0} / {1}"},
    "Ui_Field": {"en": "Field", "ja": "記入欄", "zh-Hans": "填写栏", "zh-Hant": "填寫欄", "ko": "입력란", "fr": "Champ", "de": "Feld"},
    "Ui_FieldsOnPage": {"en": "Fields on this page", "ja": "このページの記入欄", "zh-Hans": "本页的填写栏", "zh-Hant": "本頁的填寫欄", "ko": "이 페이지의 입력란", "fr": "Champs de cette page", "de": "Felder auf dieser Seite"},
    "Ui_Name": {"en": "Name", "ja": "名前", "zh-Hans": "名称", "zh-Hant": "名稱", "ko": "이름", "fr": "Nom", "de": "Name"},
    "Ui_Type": {"en": "Type", "ja": "種類", "zh-Hans": "类型", "zh-Hant": "類型", "ko": "종류", "fr": "Type", "de": "Typ"},
    "Ui_Boxes": {"en": "Boxes", "ja": "マス数", "zh-Hans": "格数", "zh-Hant": "格數", "ko": "칸 수", "fr": "Cases", "de": "Kästchen"},
    "Ui_Align": {"en": "Alignment", "ja": "配置", "zh-Hans": "对齐", "zh-Hant": "對齊", "ko": "정렬", "fr": "Alignement", "de": "Ausrichtung"},
    "Ui_AlignLeft": {"en": "Left", "ja": "左", "zh-Hans": "左", "zh-Hant": "左", "ko": "왼쪽", "fr": "Gauche", "de": "Links"},
    "Ui_AlignCenter": {"en": "Centre", "ja": "中央", "zh-Hans": "居中", "zh-Hant": "置中", "ko": "가운데", "fr": "Centré", "de": "Mitte"},
    "Ui_AlignRight": {"en": "Right", "ja": "右", "zh-Hans": "右", "zh-Hant": "右", "ko": "오른쪽", "fr": "Droite", "de": "Rechts"},
    "Ui_Label": {"en": "Printed label", "ja": "印刷された見出し", "zh-Hans": "印在格内的标签", "zh-Hant": "印在格內的標籤", "ko": "인쇄된 레이블", "fr": "Libellé imprimé", "de": "Gedruckte Beschriftung"},
    "Ui_Prefilled": {"en": "Already printed", "ja": "印刷済みの内容", "zh-Hans": "已印内容", "zh-Hant": "已印內容", "ko": "이미 인쇄된 내용", "fr": "Déjà imprimé", "de": "Bereits gedruckt"},
    "Ui_Position": {"en": "Position (pt): x {0}, y {1}, {2} × {3}", "ja": "位置 (pt): x {0}, y {1}, {2} × {3}", "zh-Hans": "位置 (pt)：x {0}，y {1}，{2} × {3}", "zh-Hant": "位置 (pt)：x {0}，y {1}，{2} × {3}", "ko": "위치 (pt): x {0}, y {1}, {2} × {3}", "fr": "Position (pt) : x {0}, y {1}, {2} × {3}", "de": "Position (pt): x {0}, y {1}, {2} × {3}"},
    "Ui_NoSelection": {"en": "Select a field on the page or in the list.", "ja": "ページ上かリストで記入欄を選んでください。", "zh-Hans": "请在页面或列表中选择一个填写栏。", "zh-Hant": "請在頁面或清單中選擇一個填寫欄。", "ko": "페이지나 목록에서 입력란을 선택하세요.", "fr": "Sélectionnez un champ sur la page ou dans la liste.", "de": "Wählen Sie ein Feld auf der Seite oder in der Liste."},
    "Ui_Hint": {"en": "Drag a field to move it, its corner to resize it. Delete removes it, arrow keys nudge it.", "ja": "ドラッグで移動、角をドラッグで大きさを変更。Delete で削除、矢印キーで微調整。", "zh-Hans": "拖动可移动填写栏，拖动角可调整大小。Delete 删除，方向键微调。", "zh-Hant": "拖曳可移動填寫欄，拖曳角可調整大小。Delete 刪除，方向鍵微調。", "ko": "드래그하면 이동, 모서리를 드래그하면 크기 조절. Delete 로 삭제, 화살표 키로 미세 조정.", "fr": "Glissez un champ pour le déplacer, son coin pour le redimensionner. Suppr le retire, les flèches l’ajustent.", "de": "Feld ziehen zum Verschieben, Ecke ziehen zum Ändern der Größe. Entf löscht, Pfeiltasten verschieben fein."},
    "Ui_DrawHint": {"en": "Drag on the page to draw a new field.", "ja": "ページ上をドラッグして新しい記入欄を描きます。", "zh-Hans": "在页面上拖动即可画出新的填写栏。", "zh-Hant": "在頁面上拖曳即可繪製新的填寫欄。", "ko": "페이지에서 드래그해 새 입력란을 그립니다.", "fr": "Glissez sur la page pour dessiner un nouveau champ.", "de": "Auf der Seite ziehen, um ein neues Feld zu zeichnen."},
    "Ui_StartHint": {"en": "Open a PDF to start.", "ja": "PDF を開いて始めましょう。", "zh-Hans": "打开一个 PDF 开始。", "zh-Hant": "開啟一個 PDF 開始。", "ko": "PDF 를 열어 시작하세요.", "fr": "Ouvrez un PDF pour commencer.", "de": "Öffnen Sie ein PDF, um zu beginnen."},
    "Ui_Opened": {"en": "{0}: {1} pages", "ja": "{0}: {1} ページ", "zh-Hans": "{0}：{1} 页", "zh-Hant": "{0}：{1} 頁", "ko": "{0}: {1}페이지", "fr": "{0} : {1} pages", "de": "{0}: {1} Seiten"},
    "Ui_Detecting": {"en": "Detecting fields…", "ja": "記入欄を検出しています…", "zh-Hans": "正在识别填写栏…", "zh-Hant": "正在辨識填寫欄…", "ko": "입력란을 감지하는 중…", "fr": "Détection des champs…", "de": "Felder werden erkannt…"},
    "Ui_Detected": {"en": "{0} fields ({1})", "ja": "記入欄 {0} 個（{1}）", "zh-Hans": "{0} 个填写栏（{1}）", "zh-Hant": "{0} 個填寫欄（{1}）", "ko": "입력란 {0}개 ({1})", "fr": "{0} champs ({1})", "de": "{0} Felder ({1})"},
    "Ui_Saved": {"en": "Saved {0}", "ja": "{0} を保存しました", "zh-Hans": "已保存 {0}", "zh-Hant": "已儲存 {0}", "ko": "{0} 저장됨", "fr": "{0} enregistré", "de": "{0} gespeichert"},
    "Ui_Loaded": {"en": "Loaded {0} fields from {1}", "ja": "{1} から記入欄 {0} 個を読み込みました", "zh-Hans": "已从 {1} 载入 {0} 个填写栏", "zh-Hant": "已從 {1} 載入 {0} 個填寫欄", "ko": "{1} 에서 입력란 {0}개를 불러왔습니다", "fr": "{0} champs chargés depuis {1}", "de": "{0} Felder aus {1} geladen"},
    "Ui_Made": {"en": "Fillable PDF written: {0}", "ja": "記入できる PDF を書き出しました: {0}", "zh-Hans": "已生成可填写 PDF：{0}", "zh-Hant": "已產生可填寫 PDF：{0}", "ko": "작성 가능한 PDF 를 만들었습니다: {0}", "fr": "PDF remplissable créé : {0}", "de": "Ausfüllbares PDF geschrieben: {0}"},
    "Ui_Error": {"en": "Error: {0}", "ja": "エラー: {0}", "zh-Hans": "错误：{0}", "zh-Hant": "錯誤：{0}", "ko": "오류: {0}", "fr": "Erreur : {0}", "de": "Fehler: {0}"},
    "Ui_PdfFiles": {"en": "PDF files", "ja": "PDF ファイル", "zh-Hans": "PDF 文件", "zh-Hant": "PDF 檔案", "ko": "PDF 파일", "fr": "Fichiers PDF", "de": "PDF-Dateien"},
    "Ui_JsonFiles": {"en": "Field lists (JSON)", "ja": "記入欄リスト (JSON)", "zh-Hans": "填写栏列表 (JSON)", "zh-Hant": "填寫欄清單 (JSON)", "ko": "입력란 목록 (JSON)", "fr": "Listes de champs (JSON)", "de": "Feldlisten (JSON)"},
    "Ui_FontFiles": {"en": "Fonts", "ja": "フォント", "zh-Hans": "字体", "zh-Hant": "字型", "ko": "글꼴", "fr": "Polices", "de": "Schriften"},
    "UsageAi": {
        "en": 'AI (Claude by default; data is shown and confirmed before it is sent):\n  washi ai config [anthropic|openai|deepseek|local <model> [--endpoint url] [--vision yes|no]]\n  washi ai key <anthropic|openai|deepseek>          store an API key in the macOS Keychain\n  washi ai models                                   list the models the provider offers\n  washi ai name-fields <in.pdf> [--fields f.json] [-o out.json] [--yes]\n  washi ai fill <fillable.pdf> --profile me.txt [-o out.pdf] [--embed-font f] [--yes]\n  washi ai ask <in.pdf> "question" [--yes]',
        "ja": 'AI（既定は Claude。送信する内容を表示し、確認してから送ります）:\n  washi ai config [anthropic|openai|deepseek|local <モデル> [--endpoint url] [--vision yes|no]]\n  washi ai key <anthropic|openai|deepseek>          API キーを macOS のキーチェーンに保存\n  washi ai models                                   プロバイダのモデル一覧\n  washi ai name-fields <in.pdf> [--fields f.json] [-o out.json] [--yes]\n  washi ai fill <fillable.pdf> --profile me.txt [-o out.pdf] [--embed-font f] [--yes]\n  washi ai ask <in.pdf> "質問" [--yes]',
        "zh-Hans": 'AI（默认用 Claude；发送前会列出内容并请你确认）：\n  washi ai config [anthropic|openai|deepseek|local <模型> [--endpoint url] [--vision yes|no]]\n  washi ai key <anthropic|openai|deepseek>          把 API key 存进 macOS 钥匙串\n  washi ai models                                   列出提供方的模型\n  washi ai name-fields <in.pdf> [--fields f.json] [-o out.json] [--yes]\n  washi ai fill <fillable.pdf> --profile me.txt [-o out.pdf] [--embed-font f] [--yes]\n  washi ai ask <in.pdf> "问题" [--yes]',
        "zh-Hant": 'AI（預設使用 Claude；傳送前會列出內容並請你確認）：\n  washi ai config [anthropic|openai|deepseek|local <模型> [--endpoint url] [--vision yes|no]]\n  washi ai key <anthropic|openai|deepseek>          將 API key 存入 macOS 鑰匙圈\n  washi ai models                                   列出提供者的模型\n  washi ai name-fields <in.pdf> [--fields f.json] [-o out.json] [--yes]\n  washi ai fill <fillable.pdf> --profile me.txt [-o out.pdf] [--embed-font f] [--yes]\n  washi ai ask <in.pdf> "問題" [--yes]',
        "ko": 'AI (기본값 Claude, 보내기 전에 내용을 보여 주고 확인을 받습니다):\n  washi ai config [anthropic|openai|deepseek|local <모델> [--endpoint url] [--vision yes|no]]\n  washi ai key <anthropic|openai|deepseek>          API 키를 macOS 키체인에 저장\n  washi ai models                                   제공자의 모델 목록\n  washi ai name-fields <in.pdf> [--fields f.json] [-o out.json] [--yes]\n  washi ai fill <fillable.pdf> --profile me.txt [-o out.pdf] [--embed-font f] [--yes]\n  washi ai ask <in.pdf> "질문" [--yes]',
        "fr": 'IA (Claude par défaut ; les données sont montrées et confirmées avant l’envoi) :\n  washi ai config [anthropic|openai|deepseek|local <modèle> [--endpoint url] [--vision yes|no]]\n  washi ai key <anthropic|openai|deepseek>          enregistre une clé d’API dans le trousseau macOS\n  washi ai models                                   liste les modèles du fournisseur\n  washi ai name-fields <in.pdf> [--fields f.json] [-o out.json] [--yes]\n  washi ai fill <fillable.pdf> --profile moi.txt [-o out.pdf] [--embed-font f] [--yes]\n  washi ai ask <in.pdf> "question" [--yes]',
        "de": 'KI (standardmäßig Claude; Daten werden vor dem Senden gezeigt und bestätigt):\n  washi ai config [anthropic|openai|deepseek|local <Modell> [--endpoint url] [--vision yes|no]]\n  washi ai key <anthropic|openai|deepseek>          API-Schlüssel im macOS-Schlüsselbund speichern\n  washi ai models                                   Modelle des Anbieters auflisten\n  washi ai name-fields <in.pdf> [--fields f.json] [-o out.json] [--yes]\n  washi ai fill <fillable.pdf> --profile ich.txt [-o out.pdf] [--embed-font f] [--yes]\n  washi ai ask <in.pdf> "Frage" [--yes]',
    },
    "Ai_Config": {
        "en": 'AI model: {0}   API key: {1}   settings: {2}',
        "ja": 'AI モデル: {0}   API キー: {1}   設定: {2}',
        "zh-Hans": 'AI 模型：{0}   API key：{1}   设置：{2}',
        "zh-Hant": 'AI 模型：{0}   API key：{1}   設定：{2}',
        "ko": 'AI 모델: {0}   API 키: {1}   설정: {2}',
        "fr": 'Modèle d’IA : {0}   clé d’API : {1}   réglages : {2}',
        "de": 'KI-Modell: {0}   API-Schlüssel: {1}   Einstellungen: {2}',
    },
    "Ai_KeyFound": {
        "en": 'found',
        "ja": 'あり',
        "zh-Hans": '已找到',
        "zh-Hant": '已找到',
        "ko": '있음',
        "fr": 'trouvée',
        "de": 'gefunden',
    },
    "Ai_KeyMissing": {
        "en": 'missing',
        "ja": 'なし',
        "zh-Hans": '未设置',
        "zh-Hant": '未設定',
        "ko": '없음',
        "fr": 'absente',
        "de": 'fehlt',
    },
    "Ai_Saved": {
        "en": 'Saved {0}',
        "ja": '{0} を保存しました',
        "zh-Hans": '已保存 {0}',
        "zh-Hant": '已儲存 {0}',
        "ko": '{0} 저장됨',
        "fr": '{0} enregistré',
        "de": '{0} gespeichert',
    },
    "Ai_KeyPrompt": {
        "en": 'Paste the {0} API key (it is not shown): ',
        "ja": '{0} の API キーを貼り付けてください（表示されません）: ',
        "zh-Hans": '请粘贴 {0} 的 API key（不会显示）：',
        "zh-Hant": '請貼上 {0} 的 API key（不會顯示）：',
        "ko": '{0} API 키를 붙여 넣으세요 (표시되지 않음): ',
        "fr": 'Collez la clé d’API {0} (elle reste masquée) : ',
        "de": '{0}-API-Schlüssel einfügen (wird nicht angezeigt): ',
    },
    "Ai_KeyStored": {
        "en": 'Stored in the macOS Keychain (service "washi", account "{0}").',
        "ja": 'macOS のキーチェーンに保存しました（サービス "washi"、アカウント "{0}"）。',
        "zh-Hans": '已存入 macOS 钥匙串（服务 "washi"，账户 "{0}"）。',
        "zh-Hant": '已存入 macOS 鑰匙圈（服務 "washi"，帳號 "{0}"）。',
        "ko": 'macOS 키체인에 저장했습니다 (서비스 "washi", 계정 "{0}").',
        "fr": 'Enregistrée dans le trousseau macOS (service « washi », compte « {0} »).',
        "de": 'Im macOS-Schlüsselbund gespeichert (Dienst „washi“, Konto „{0}“).',
    },
    "Ai_WillSend": {
        "en": 'This sends to {0}: {1}.',
        "ja": '{0} に送信します: {1}',
        "zh-Hans": '将发送给 {0}：{1}。',
        "zh-Hant": '將傳送給 {0}：{1}。',
        "ko": '{0} 에 보냅니다: {1}.',
        "fr": 'Envoi à {0} : {1}.',
        "de": 'Wird an {0} gesendet: {1}.',
    },
    "Ai_Local": {
        "en": 'Local model {0}: nothing leaves this computer.',
        "ja": 'ローカルモデル {0}: このコンピュータの外には何も送られません。',
        "zh-Hans": '本地模型 {0}：不会有任何数据离开这台电脑。',
        "zh-Hant": '本機模型 {0}：不會有任何資料離開這台電腦。',
        "ko": '로컬 모델 {0}: 이 컴퓨터 밖으로 아무것도 나가지 않습니다.',
        "fr": 'Modèle local {0} : rien ne quitte cet ordinateur.',
        "de": 'Lokales Modell {0}: nichts verlässt diesen Computer.',
    },
    "Ai_Confirm": {
        "en": 'Send it? [y/N] ',
        "ja": '送信しますか？ [y/N] ',
        "zh-Hans": '要发送吗？[y/N] ',
        "zh-Hant": '要傳送嗎？[y/N] ',
        "ko": '보낼까요? [y/N] ',
        "fr": 'Envoyer ? [o/N] ',
        "de": 'Senden? [j/N] ',
    },
    "Ai_NeedYes": {
        "en": 'Nothing was sent: add --yes to confirm when there is no terminal to ask.',
        "ja": '送信していません。確認できる端末がないときは --yes を付けてください。',
        "zh-Hans": '未发送：没有可以询问的终端时，请加上 --yes 确认。',
        "zh-Hant": '未傳送：沒有可以詢問的終端機時，請加上 --yes 確認。',
        "ko": '보내지 않았습니다: 물어볼 터미널이 없으면 --yes 를 붙여 확인하세요.',
        "fr": 'Rien n’a été envoyé : ajoutez --yes pour confirmer quand aucun terminal ne peut poser la question.',
        "de": 'Nichts gesendet: ohne Terminal zum Nachfragen mit --yes bestätigen.',
    },
    "Ai_Cancelled": {
        "en": 'Cancelled. Nothing was sent.',
        "ja": 'キャンセルしました。何も送信していません。',
        "zh-Hans": '已取消，没有发送任何内容。',
        "zh-Hant": '已取消，沒有傳送任何內容。',
        "ko": '취소했습니다. 아무것도 보내지 않았습니다.',
        "fr": 'Annulé. Rien n’a été envoyé.',
        "de": 'Abgebrochen. Nichts wurde gesendet.',
    },
    "Ai_Working": {
        "en": 'Asking {0}…',
        "ja": '{0} に問い合わせています…',
        "zh-Hans": '正在询问 {0}…',
        "zh-Hant": '正在詢問 {0}…',
        "ko": '{0} 에 묻는 중…',
        "fr": 'Interrogation de {0}…',
        "de": '{0} wird gefragt…',
    },
    "Ai_Named": {
        "en": '{0}: {1} fields, {2} renamed, {3} types changed -> {4}',
        "ja": '{0}: 記入欄 {1} 個、名前変更 {2}、種類変更 {3} -> {4}',
        "zh-Hans": '{0}：{1} 个填写栏，改名 {2} 个，改类型 {3} 个 -> {4}',
        "zh-Hant": '{0}：{1} 個填寫欄，改名 {2} 個，改類型 {3} 個 -> {4}',
        "ko": '{0}: 입력란 {1}개, 이름 변경 {2}, 종류 변경 {3} -> {4}',
        "fr": '{0} : {1} champs, {2} renommés, {3} types modifiés -> {4}',
        "de": '{0}: {1} Felder, {2} umbenannt, {3} Typen geändert -> {4}',
    },
    "Ai_Filled": {
        "en": 'Filled {0} fields -> {1}',
        "ja": '記入欄 {0} 個に記入しました -> {1}',
        "zh-Hans": '已填写 {0} 个栏位 -> {1}',
        "zh-Hant": '已填寫 {0} 個欄位 -> {1}',
        "ko": '입력란 {0}개를 채웠습니다 -> {1}',
        "fr": '{0} champs remplis -> {1}',
        "de": '{0} Felder ausgefüllt -> {1}',
    },
    "Ai_Note": {
        "en": '  note: {0}',
        "ja": '  メモ: {0}',
        "zh-Hans": '  说明：{0}',
        "zh-Hant": '  說明：{0}',
        "ko": '  메모: {0}',
        "fr": '  remarque : {0}',
        "de": '  Hinweis: {0}',
    },
    "Ai_Error": {
        "en": 'AI: {0}',
        "ja": 'AI: {0}',
        "zh-Hans": 'AI：{0}',
        "zh-Hant": 'AI：{0}',
        "ko": 'AI: {0}',
        "fr": 'IA : {0}',
        "de": 'KI: {0}',
    },
    "Ai_NoProfile": {
        "en": '--profile <file> is required: a text or JSON file with the data to fill in.',
        "ja": '--profile <ファイル> が必要です。記入するデータを書いたテキストか JSON ファイルです。',
        "zh-Hans": '需要 --profile <文件>：写有要填写内容的文本或 JSON 文件。',
        "zh-Hant": '需要 --profile <檔案>：寫有要填寫內容的文字或 JSON 檔案。',
        "ko": '--profile <파일> 이 필요합니다: 채울 데이터가 담긴 텍스트 또는 JSON 파일.',
        "fr": '--profile <fichier> est requis : un fichier texte ou JSON avec les données à remplir.',
        "de": '--profile <Datei> ist erforderlich: eine Text- oder JSON-Datei mit den Daten.',
    },
    "Ui_AiName": {
        "en": 'Name fields with AI',
        "ja": 'AI で名前を付ける',
        "zh-Hans": '用 AI 命名',
        "zh-Hant": '用 AI 命名',
        "ko": 'AI 로 이름 짓기',
        "fr": 'Nommer avec l’IA',
        "de": 'Mit KI benennen',
    },
    "Ui_AiConfirmTitle": {
        "en": 'Send to the AI?',
        "ja": 'AI に送信しますか？',
        "zh-Hans": '发送给 AI？',
        "zh-Hant": '傳送給 AI？',
        "ko": 'AI 에 보낼까요?',
        "fr": 'Envoyer à l’IA ?',
        "de": 'An die KI senden?',
    },
    "Ui_Send": {
        "en": 'Send',
        "ja": '送信',
        "zh-Hans": '发送',
        "zh-Hant": '傳送',
        "ko": '보내기',
        "fr": 'Envoyer',
        "de": 'Senden',
    },
    "Ui_Cancel": {
        "en": 'Cancel',
        "ja": 'キャンセル',
        "zh-Hans": '取消',
        "zh-Hant": '取消',
        "ko": '취소',
        "fr": 'Annuler',
        "de": 'Abbrechen',
    },
    "Ui_PrefilledDrawn": {
        "en": "Something is printed here (drawn as graphics, not as text). The button below reads it and makes it an editable value.",
        "ja": "ここには何かが印刷されています（文字ではなく図形として描画）。下のボタンで読み取り、編集できる値にできます。",
        "zh-Hans": "这里印有内容（以图形而非文字绘制）。点下方按钮即可读取并变为可编辑的值。",
        "zh-Hant": "這裡印有內容（以圖形而非文字繪製）。點下方按鈕即可讀取並變為可編輯的值。",
        "ko": "여기에 무언가 인쇄되어 있습니다(텍스트가 아닌 그래픽). 아래 버튼으로 읽어 편집 가능한 값으로 바꿀 수 있습니다.",
        "fr": "Quelque chose est imprimé ici (dessiné en graphisme, pas en texte). Le bouton ci-dessous le lit et en fait une valeur modifiable.",
        "de": "Hier ist etwas gedruckt (als Grafik, nicht als Text). Die Schaltfläche unten liest es und macht es zu einem bearbeitbaren Wert.",
    },
    "Ui_PrefilledUnreadable": {
        "en": "Text is printed here, but the PDF's text layer cannot be read (its fonts have no Unicode mapping). The button below reads it by OCR and makes it an editable value.",
        "ja": "ここには文字が印刷されていますが、PDF の文字情報を読み取れません（フォントに Unicode 対応表がありません）。下のボタンで OCR により読み取り、編集できる値にできます。",
        "zh-Hans": "这里印有文字，但 PDF 的文字层无法读取（字体没有 Unicode 对照表）。点下方按钮即可用 OCR 读取，并变为可编辑的值。",
        "zh-Hant": "這裡印有文字，但 PDF 的文字層無法讀取（字型沒有 Unicode 對照表）。點下方按鈕即可用 OCR 讀取，並變為可編輯的值。",
        "ko": "여기에 글자가 인쇄되어 있지만 PDF 의 텍스트 레이어를 읽을 수 없습니다(글꼴에 Unicode 매핑 없음). 아래 버튼으로 OCR 로 읽어 편집 가능한 값으로 바꿀 수 있습니다.",
        "fr": "Du texte est imprimé ici, mais la couche texte du PDF est illisible (polices sans correspondance Unicode). Le bouton ci-dessous le lit par OCR et en fait une valeur modifiable.",
        "de": "Hier ist Text gedruckt, aber die Textebene des PDFs ist nicht lesbar (Schriften ohne Unicode-Zuordnung). Die Schaltfläche unten liest ihn per OCR und macht ihn zu einem bearbeitbaren Wert.",
    },
    "Kind_text": {"en": "text", "ja": "テキスト", "zh-Hans": "文本", "zh-Hant": "文字", "ko": "텍스트", "fr": "texte", "de": "Text"},
    "Kind_amount": {"en": "amount", "ja": "金額", "zh-Hans": "金额", "zh-Hant": "金額", "ko": "금액", "fr": "montant", "de": "Betrag"},
    "Kind_comb": {"en": "comb", "ja": "マス目", "zh-Hans": "分格", "zh-Hant": "分格", "ko": "칸 나눔", "fr": "cases", "de": "Kästchen"},
    "Kind_checkbox": {"en": "checkbox", "ja": "チェックボックス", "zh-Hans": "复选框", "zh-Hant": "核取方塊", "ko": "체크박스", "fr": "case à cocher", "de": "Kontrollkästchen"},
    "Kind_radio": {"en": "radio button", "ja": "ラジオボタン", "zh-Hans": "单选按钮", "zh-Hant": "選項按鈕", "ko": "라디오 버튼", "fr": "bouton radio", "de": "Optionsfeld"},
    "Kind_choice": {"en": "drop-down list", "ja": "ドロップダウン", "zh-Hans": "下拉列表", "zh-Hant": "下拉式清單", "ko": "드롭다운 목록", "fr": "liste déroulante", "de": "Dropdown-Liste"},
    "Kind_signature": {"en": "signature", "ja": "署名", "zh-Hans": "签名", "zh-Hant": "簽名", "ko": "서명", "fr": "signature", "de": "Unterschrift"},
    "UsagePdf": {
        "en": (
            'Pages and OCR:\n'
            '  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      join files (form fields are kept)\n'
            '  washi pdf from-images <a.jpg> <b.png> ... [--page a4|letter|image] [--margin pt] [--dpi 150] -o out.pdf   photos and scans into pages (JPEG kept as it is)\n'
            '  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n'
            '  washi pdf organize <in.pdf> [--pages 3,1-2,5] [--rotate 2:90,5:-90] -o out.pdf\n'
            '                                          reorder, drop (leave out) and turn pages (form fields are kept)\n'
            '  washi pdf crop <in.pdf> (--margins top,right,bottom,left | --trim [--padding 6]) [--pages 1-3] [-o out.pdf]   cut margins (points) or trim to the content\n'
            '  washi pdf export-images <in.pdf> [--format png|jpeg] [--dpi 150] [--pages 1-3] [--quality 90] [-o folder]   pages as image files\n'
            '  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n'
            '                                          make scanned pages searchable (an invisible text layer; the page image is unchanged)\n'
            '  washi pdf erase <in.pdf> --box page:x0,y0,x1,y1 [--box ...] [-o out.pdf] [--all-ink] [--rules]\n'
            '                                          remove printed text/images under boxes for real (points, top-left origin; coloured form printing stays)\n'
            '  washi pdf protect <in.pdf> -o out.pdf [--open] [--allow print,copy,edit,comment,fill,assemble|none]\n'
            '                                          add a password to open (--open) and/or limits (--allow: what stays allowed), AES-256;\n'
            '                                          passwords are asked for, or read from WASHI_OPEN_PASSWORD / WASHI_PERMISSIONS_PASSWORD\n'
            '  washi pdf unlock <in.pdf> -o out.pdf   remove the password (asked for, or WASHI_PASSWORD)\n'
            '  washi pdf clean <in.pdf> [--list] [--only metadata,attachments,scripts,comments,private] [-o out.pdf]\n'
            '                                          remove hidden information (--list only shows it)\n'
            '  washi pdf redact <in.pdf> [--find word ...] [--pattern email,phone,number] [--regex RE] [--box page:x0,y0,x1,y1] [--case] [--whole-words] [--list] [-o out.pdf]\n'
            '                                          remove words, personal data or areas for real and cover them in black (--list only shows the matches)\n'
            '  washi pdf find <in.pdf> <text> [--case] [--whole-words]   where the text is, page by page\n'
            '  washi pdf links <in.pdf> [--find-addresses] [--add page:x0,y0,x1,y1=target ...] [--remove 1-2,3-1] [-o out.pdf]   list links, or add (web/mail address, page number) and remove them\n'
            '  washi pdf images <in.pdf> [--add page:x0,y0,x1,y1=photo.jpg ...] [--replace 1-2=logo.png ...] [--remove 3-1] [-o out.pdf]   list, add, replace or remove the images on the pages\n'
            '  washi pdf compare <old.pdf> <new.pdf> [-o marked.pdf]   what changed: words, pages, pictures (-o: the new version with each change marked)\n'
            '  washi pdf replace-text <in.pdf> (--find old | --box page:x0,y0,x1,y1) --with new [--page n] [--ignore-case] [--whole-words] [-o out.pdf]\n'
            '                                          change text on the page (one line at a time; checked before it is kept)\n'
            '                 --paragraph "text in it" --with "new paragraph": set the whole paragraph again in its width\n'
            '  washi pdf stamp <in.pdf> [--header "left|center|right"] [--footer "…"] [--size 9] [--pages 1-3] [--bates PREFIX [--bates-start 1] [--bates-digits 6]]\n'
            '                 [--watermark TEXT [--opacity 0.3] [--angle 45] [--behind]] [-o out.pdf]   headers, footers, page and Bates numbers ({page} {pages} {date} {file} {bates}), a watermark\n'
            '                 --replace-existing: take out what Washi added before; washi pdf stamp <in.pdf> --remove headers|watermarks|all [-o out.pdf]: only take it out\n'
            '  washi pdf bookmarks <in.pdf> [--set outline.txt | --clear] [-o out.pdf]\n'
            '                                          list the bookmarks (one per line: two spaces per level, a tab, the page),\n'
            '                                          or write a copy with the ones in a file of that form\n'
            '  washi pdf print <in.pdf> [--printer name] [--copies n] [--pages 1-3,5] [--two-sided] [--gray] [--actual-size]\n'
            '  washi pdf printers                      list the printers\n'
            '  washi pdf compress <in.pdf> [--level light|standard|strong] [-o out.pdf]   a smaller copy (pages compared before it is kept)\n'
            '  washi pdf flatten <in.pdf> [--fields-only | --comments-only] [-o out.pdf]   draw fields and comments into the page (checked before it is kept)\n'
            '  washi pdf pdfa <in.pdf> [--check] [--flatten] [-o out.pdf]   a PDF/A-2b copy for archiving (--check only lists what stands in the way)\n'
            '  washi pdf sign <in.pdf> --id id.p12 [--field name | --page n --box x0,y0,x1,y1] [--reason …] [--location …] [--tsa URL] [--certify …] [--ltv] [-o out.pdf]\n'
            '                                          sign with a digital ID (password from WASHI_ID_PASSWORD or asked for)\n'
            '            certify: --certify no-changes|forms|comments; --ltv adds long-term validation data after signing\n'
            '  washi pdf verify <in.pdf> [--trust ca.cer]   check the signatures in a file\n'
            '  washi pdf ltv <in.pdf> [--tsa URL] [--trust ca.cer] [-o out.pdf]   add long-term validation data (and a document time-stamp) to the signatures\n'
            '  washi pdf labels <in.pdf> [--set 1:i,5:1 | --clear] [-o out.pdf]   page labels (as the viewer numbers pages)\n'
            '  washi pdf attachments <in.pdf> [--add a,b [--description …] | --remove a,b] [-o out.pdf] | --extract dir [--name a]\n'
            '  washi pdf layers <in.pdf> [--show a,b] [--hide c] [-o out.pdf]   list layers, or set which show\n'
            '  washi pdf protect <in.pdf> --to a.cer,b.cer [-o out.pdf]   encrypt for certificates;  washi pdf unlock <in.pdf> --id id.p12   open it\n'
            '  washi pdf data <in.pdf> [-o data.xfdf|.fdf|.csv|.json]   the form\'s values (to a data file)\n'
            '  washi pdf data <in.pdf> --import data.xfdf [-o out.pdf]   |   --rows table.csv [-o folder] [--name-column col]   one copy per row\n'
            '  washi pdf collect <a.pdf> <b.pdf|folder> ... -o table.csv   many filled forms into one table\n'
            '  washi pdf make-id --name "…" [--email …] -o id.p12   make a self-signed digital ID'
        ),
        "ja": (
            'ページと OCR:\n'
            '  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      ファイルを結合（記入欄はそのまま残ります）\n'
            '  washi pdf from-images <a.jpg> <b.png> ... [--page a4|letter|image] [--margin pt] [--dpi 150] -o out.pdf   写真・スキャン画像を PDF のページに（JPEG はそのまま）\n'
            '  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n'
            '  washi pdf organize <in.pdf> [--pages 3,1-2,5] [--rotate 2:90,5:-90] -o out.pdf\n'
            '                                          ページの並べ替え・削除（指定しないページ）・回転（記入欄はそのまま残ります）\n'
            '  washi pdf crop <in.pdf> (--margins 上,右,下,左 | --trim [--padding 6]) [--pages 1-3] [-o out.pdf]   余白を切る（ポイント）か、内容に合わせて切り詰める\n'
            '  washi pdf export-images <in.pdf> [--format png|jpeg] [--dpi 150] [--pages 1-3] [--quality 90] [-o フォルダー]   ページを画像ファイルに\n'
            '  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n'
            '                                          スキャンしたページを検索できるようにする（見えない文字層を追加。ページの画像は変わりません）\n'
            '  washi pdf erase <in.pdf> --box page:x0,y0,x1,y1 [--box ...] [-o out.pdf] [--all-ink] [--rules]\n'
            '                                          枠内の印字（文字・画像）を本当に削除（ポイント単位・左上原点。色付きの様式の印刷は残ります）\n'
            '  washi pdf protect <in.pdf> -o out.pdf [--open] [--allow print,copy,edit,comment,fill,assemble|none]\n'
            '                                          開くパスワード（--open）や制限（--allow: 許可する操作）を付ける。AES-256。\n'
            '                                          パスワードは入力を求めるか、WASHI_OPEN_PASSWORD / WASHI_PERMISSIONS_PASSWORD から読みます\n'
            '  washi pdf unlock <in.pdf> -o out.pdf   パスワードを外す（入力を求めるか WASHI_PASSWORD）\n'
            '  washi pdf clean <in.pdf> [--list] [--only metadata,attachments,scripts,comments,private] [-o out.pdf]\n'
            '                                          隠れた情報を削除（--list は表示だけ）\n'
            '  washi pdf redact <in.pdf> [--find 語句 ...] [--pattern email,phone,number] [--regex 正規表現] [--box page:x0,y0,x1,y1] [--case] [--whole-words] [--list] [-o out.pdf]\n'
            '                                          語句・個人情報・範囲を本当に削除して黒く塗る（--list は一致箇所の表示だけ）\n'
            '  washi pdf find <in.pdf> <語句> [--case] [--whole-words]   語句のある場所をページごとに表示\n'
            '  washi pdf links <in.pdf> [--find-addresses] [--add page:x0,y0,x1,y1=リンク先 ...] [--remove 1-2,3-1] [-o out.pdf]   リンクを一覧表示、または追加（URL・メール・ページ番号）と削除\n'
            '  washi pdf images <in.pdf> [--add page:x0,y0,x1,y1=photo.jpg ...] [--replace 1-2=logo.png ...] [--remove 3-1] [-o out.pdf]   ページの画像を一覧・追加・差し替え・削除\n'
            '  washi pdf compare <旧.pdf> <新.pdf> [-o marked.pdf]   変わったところ（語句・ページ・図や画像）を表示（-o：変更箇所に注釈を付けた新版）\n'
            '  washi pdf replace-text <in.pdf> (--find 元の語句 | --box page:x0,y0,x1,y1) --with 新しい語句 [--page n] [--ignore-case] [--whole-words] [-o out.pdf]\n'
            '                                          ページの文字を書き換える（1 行ずつ。保存前に確認）\n'
            '                 --paragraph "段落内の語句" --with "新しい段落"：段落全体を元の幅で組み直す\n'
            '  washi pdf stamp <in.pdf> [--header "左|中央|右"] [--footer "…"] [--size 9] [--pages 1-3] [--bates 接頭辞 [--bates-start 1] [--bates-digits 6]]\n'
            '                 [--watermark 文字 [--opacity 0.3] [--angle 45] [--behind]] [-o out.pdf]   ヘッダー・フッター・ページ番号・ベイツ番号（{page} {pages} {date} {file} {bates}）、透かし\n'
            '                 --replace-existing：Washi が以前に入れたものを外してから追加；washi pdf stamp <in.pdf> --remove headers|watermarks|all [-o out.pdf]：外すだけ\n'
            '  washi pdf bookmarks <in.pdf> [--set outline.txt | --clear] [-o out.pdf]\n'
            '                                          しおりを一覧表示（1 行に 1 つ: 階層ごとに空白 2 つ、タブ、ページ）、\n'
            '                                          または同じ形式のファイルのしおりでコピーを作成\n'
            '  washi pdf print <in.pdf> [--printer 名前] [--copies 部数] [--pages 1-3,5] [--two-sided] [--gray] [--actual-size]\n'
            '  washi pdf printers                      プリンターの一覧\n'
            '  washi pdf compress <in.pdf> [--level light|standard|strong] [-o out.pdf]   小さなコピーを作成（保存前にページを比較）\n'
            '  washi pdf flatten <in.pdf> [--fields-only | --comments-only] [-o out.pdf]   記入欄とコメントをページに描き込んで確定（保存前に確認）\n'
            '  washi pdf pdfa <in.pdf> [--check] [--flatten] [-o out.pdf]   長期保存用の PDF/A-2b のコピー（--check は直す点の一覧だけ）\n'
            '  washi pdf sign <in.pdf> --id id.p12 [--field 名前 | --page n --box x0,y0,x1,y1] [--reason …] [--location …] [--tsa URL] [--certify …] [--ltv] [-o out.pdf]\n'
            '                                          デジタル ID で署名（パスワードは WASHI_ID_PASSWORD か入力）\n'
            '            証明署名: --certify no-changes|forms|comments、--ltv で署名後に長期検証情報を追加\n'
            '  washi pdf verify <in.pdf> [--trust ca.cer]   ファイルの署名を確認\n'
            '  washi pdf ltv <in.pdf> [--tsa URL] [--trust ca.cer] [-o out.pdf]   署名に長期検証情報（と文書タイムスタンプ）を追加\n'
            '  washi pdf labels <in.pdf> [--set 1:i,5:1 | --clear] [-o out.pdf]   ページ番号の表示（ビューアでの番号）\n'
            '  washi pdf attachments <in.pdf> [--add a,b [--description …] | --remove a,b] [-o out.pdf] | --extract フォルダ [--name a]\n'
            '  washi pdf layers <in.pdf> [--show a,b] [--hide c] [-o out.pdf]   レイヤーの一覧、表示の設定\n'
            '  washi pdf protect <in.pdf> --to a.cer,b.cer [-o out.pdf]   証明書で暗号化；washi pdf unlock <in.pdf> --id id.p12   開く\n'
            '  washi pdf data <in.pdf> [-o data.xfdf|.fdf|.csv|.json]   フォームの値（データファイルへ）\n'
            '  washi pdf data <in.pdf> --import data.xfdf [-o out.pdf]   |   --rows 表.csv [-o フォルダ] [--name-column 列]   1 行ごとにコピー\n'
            '  washi pdf collect <a.pdf> <b.pdf|フォルダ> ... -o 表.csv   記入済みフォームを 1 つの表に集計\n'
            '  washi pdf make-id --name "…" [--email …] -o id.p12   自己署名のデジタル ID を作成'
        ),
        "zh-Hans": (
            '页面与 OCR：\n'
            '  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      合并文件（保留表单栏位）\n'
            '  washi pdf from-images <a.jpg> <b.png> ... [--page a4|letter|image] [--margin pt] [--dpi 150] -o out.pdf   把照片和扫描图片做成 PDF 页面（JPEG 原样保留）\n'
            '  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n'
            '  washi pdf organize <in.pdf> [--pages 3,1-2,5] [--rotate 2:90,5:-90] -o out.pdf\n'
            '                                          调整页面顺序、删除（不列出的页）和旋转页面（保留表单栏位）\n'
            '  washi pdf crop <in.pdf> (--margins 上,右,下,左 | --trim [--padding 6]) [--pages 1-3] [-o out.pdf]   裁掉边距（点）或按内容裁剪\n'
            '  washi pdf export-images <in.pdf> [--format png|jpeg] [--dpi 150] [--pages 1-3] [--quality 90] [-o 文件夹]   将页面存为图片文件\n'
            '  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n'
            '                                          让扫描页可以搜索（加一层看不见的文字，页面图像不变）\n'
            '  washi pdf erase <in.pdf> --box page:x0,y0,x1,y1 [--box ...] [-o out.pdf] [--all-ink] [--rules]\n'
            '                                          真正删除框内已印的文字和图像（单位为点，左上角为原点；彩色的表格印刷保留）\n'
            '  washi pdf protect <in.pdf> -o out.pdf [--open] [--allow print,copy,edit,comment,fill,assemble|none]\n'
            '                                          添加打开密码（--open）和/或限制（--allow：仍允许的操作），AES-256；\n'
            '                                          密码会提示输入，或从 WASHI_OPEN_PASSWORD / WASHI_PERMISSIONS_PASSWORD 读取\n'
            '  washi pdf unlock <in.pdf> -o out.pdf   解除密码（提示输入，或 WASHI_PASSWORD）\n'
            '  washi pdf clean <in.pdf> [--list] [--only metadata,attachments,scripts,comments,private] [-o out.pdf]\n'
            '                                          删除隐藏信息（--list 只列出）\n'
            '  washi pdf redact <in.pdf> [--find 词 ...] [--pattern email,phone,number] [--regex 正则] [--box page:x0,y0,x1,y1] [--case] [--whole-words] [--list] [-o out.pdf]\n'
            '                                          真正删除词语、个人信息或区域并涂黑（--list 只列出匹配处）\n'
            '  washi pdf find <in.pdf> <文字> [--case] [--whole-words]   逐页列出文字所在位置\n'
            '  washi pdf links <in.pdf> [--find-addresses] [--add page:x0,y0,x1,y1=目标 ...] [--remove 1-2,3-1] [-o out.pdf]   列出链接，或添加（网址、邮箱、页码）和删除链接\n'
            '  washi pdf images <in.pdf> [--add page:x0,y0,x1,y1=photo.jpg ...] [--replace 1-2=logo.png ...] [--remove 3-1] [-o out.pdf]   列出、添加、替换或删除页面上的图片\n'
            '  washi pdf compare <旧.pdf> <新.pdf> [-o marked.pdf]   列出变化（文字、页面、图片）（-o：在新版上用批注标出每处变化）\n'
            '  washi pdf replace-text <in.pdf> (--find 原文字 | --box page:x0,y0,x1,y1) --with 新文字 [--page n] [--ignore-case] [--whole-words] [-o out.pdf]\n'
            '                                          修改页面上的文字（一次一行；保存前会检查）\n'
            '                 --paragraph "段落中的文字" --with "新段落"：按原宽度重新排版整个段落\n'
            '  washi pdf stamp <in.pdf> [--header "左|中|右"] [--footer "…"] [--size 9] [--pages 1-3] [--bates 前缀 [--bates-start 1] [--bates-digits 6]]\n'
            '                 [--watermark 文字 [--opacity 0.3] [--angle 45] [--behind]] [-o out.pdf]   页眉、页脚、页码、贝茨编号（{page} {pages} {date} {file} {bates}）、水印\n'
            '                 --replace-existing：先去掉 Washi 以前加入的内容；washi pdf stamp <in.pdf> --remove headers|watermarks|all [-o out.pdf]：只去掉\n'
            '  washi pdf bookmarks <in.pdf> [--set outline.txt | --clear] [-o out.pdf]\n'
            '                                          列出书签（每行一个：每级缩进两个空格，制表符，页码），\n'
            '                                          或用同样格式的文件写出带书签的副本\n'
            '  washi pdf print <in.pdf> [--printer 名称] [--copies 份数] [--pages 1-3,5] [--two-sided] [--gray] [--actual-size]\n'
            '  washi pdf printers                      列出打印机\n'
            '  washi pdf compress <in.pdf> [--level light|standard|strong] [-o out.pdf]   生成更小的副本（保存前逐页比对）\n'
            '  washi pdf flatten <in.pdf> [--fields-only | --comments-only] [-o out.pdf]   把栏位和注释画进页面（保存前检查）\n'
            '  washi pdf pdfa <in.pdf> [--check] [--flatten] [-o out.pdf]   用于长期保存的 PDF/A-2b 副本（--check 只列出需处理的地方）\n'
            '  washi pdf sign <in.pdf> --id id.p12 [--field 名称 | --page n --box x0,y0,x1,y1] [--reason …] [--location …] [--tsa URL] [--certify …] [--ltv] [-o out.pdf]\n'
            '                                          用数字标识签名（密码取自 WASHI_ID_PASSWORD 或提示输入）\n'
            '            认证签名：--certify no-changes|forms|comments；--ltv 在签名后添加长期验证信息\n'
            '  washi pdf verify <in.pdf> [--trust ca.cer]   核实文件中的签名\n'
            '  washi pdf ltv <in.pdf> [--tsa URL] [--trust ca.cer] [-o out.pdf]   为签名添加长期验证信息（及文档时间戳）\n'
            '  washi pdf labels <in.pdf> [--set 1:i,5:1 | --clear] [-o out.pdf]   页码标签（查看器显示的页码）\n'
            '  washi pdf attachments <in.pdf> [--add a,b [--description …] | --remove a,b] [-o out.pdf] | --extract 文件夹 [--name a]\n'
            '  washi pdf layers <in.pdf> [--show a,b] [--hide c] [-o out.pdf]   列出图层，或设置显示\n'
            '  washi pdf protect <in.pdf> --to a.cer,b.cer [-o out.pdf]   用证书加密；washi pdf unlock <in.pdf> --id id.p12   打开\n'
            '  washi pdf data <in.pdf> [-o data.xfdf|.fdf|.csv|.json]   表单的值（写入数据文件）\n'
            '  washi pdf data <in.pdf> --import data.xfdf [-o out.pdf]   |   --rows 表格.csv [-o 文件夹] [--name-column 列]   每行一份副本\n'
            '  washi pdf collect <a.pdf> <b.pdf|文件夹> ... -o 表格.csv   将已填表单汇总为一个表格\n'
            '  washi pdf make-id --name "…" [--email …] -o id.p12   创建自签名数字标识'
        ),
        "zh-Hant": (
            '頁面與 OCR：\n'
            '  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      合併檔案（保留表單欄位）\n'
            '  washi pdf from-images <a.jpg> <b.png> ... [--page a4|letter|image] [--margin pt] [--dpi 150] -o out.pdf   把照片和掃描圖片做成 PDF 頁面（JPEG 原樣保留）\n'
            '  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n'
            '  washi pdf organize <in.pdf> [--pages 3,1-2,5] [--rotate 2:90,5:-90] -o out.pdf\n'
            '                                          調整頁面順序、刪除（不列出的頁）和旋轉頁面（保留表單欄位）\n'
            '  washi pdf crop <in.pdf> (--margins 上,右,下,左 | --trim [--padding 6]) [--pages 1-3] [-o out.pdf]   裁掉邊界（點）或依內容裁切\n'
            '  washi pdf export-images <in.pdf> [--format png|jpeg] [--dpi 150] [--pages 1-3] [--quality 90] [-o 資料夾]   將頁面存為圖片檔\n'
            '  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n'
            '                                          讓掃描頁可以搜尋（加一層看不見的文字，頁面影像不變）\n'
            '  washi pdf erase <in.pdf> --box page:x0,y0,x1,y1 [--box ...] [-o out.pdf] [--all-ink] [--rules]\n'
            '                                          真正刪除框內已印的文字和影像（單位為點，左上角為原點；彩色的表格印刷保留）\n'
            '  washi pdf protect <in.pdf> -o out.pdf [--open] [--allow print,copy,edit,comment,fill,assemble|none]\n'
            '                                          加上開啟密碼（--open）和/或限制（--allow：仍允許的操作），AES-256；\n'
            '                                          密碼會提示輸入，或從 WASHI_OPEN_PASSWORD / WASHI_PERMISSIONS_PASSWORD 讀取\n'
            '  washi pdf unlock <in.pdf> -o out.pdf   解除密碼（提示輸入，或 WASHI_PASSWORD）\n'
            '  washi pdf clean <in.pdf> [--list] [--only metadata,attachments,scripts,comments,private] [-o out.pdf]\n'
            '                                          刪除隱藏資訊（--list 只列出）\n'
            '  washi pdf redact <in.pdf> [--find 詞 ...] [--pattern email,phone,number] [--regex 規則運算式] [--box page:x0,y0,x1,y1] [--case] [--whole-words] [--list] [-o out.pdf]\n'
            '                                          真正刪除詞語、個人資訊或區域並塗黑（--list 只列出相符處）\n'
            '  washi pdf find <in.pdf> <文字> [--case] [--whole-words]   逐頁列出文字所在位置\n'
            '  washi pdf links <in.pdf> [--find-addresses] [--add page:x0,y0,x1,y1=目標 ...] [--remove 1-2,3-1] [-o out.pdf]   列出連結，或新增（網址、郵件、頁碼）和刪除連結\n'
            '  washi pdf images <in.pdf> [--add page:x0,y0,x1,y1=photo.jpg ...] [--replace 1-2=logo.png ...] [--remove 3-1] [-o out.pdf]   列出、新增、取代或刪除頁面上的圖片\n'
            '  washi pdf compare <舊.pdf> <新.pdf> [-o marked.pdf]   列出變化（文字、頁面、圖片）（-o：在新版上用註解標出每處變化）\n'
            '  washi pdf replace-text <in.pdf> (--find 原文字 | --box page:x0,y0,x1,y1) --with 新文字 [--page n] [--ignore-case] [--whole-words] [-o out.pdf]\n'
            '                                          修改頁面上的文字（一次一行；儲存前會檢查）\n'
            '                 --paragraph "段落中的文字" --with "新段落"：依原寬度重新排版整個段落\n'
            '  washi pdf stamp <in.pdf> [--header "左|中|右"] [--footer "…"] [--size 9] [--pages 1-3] [--bates 前綴 [--bates-start 1] [--bates-digits 6]]\n'
            '                 [--watermark 文字 [--opacity 0.3] [--angle 45] [--behind]] [-o out.pdf]   頁首、頁尾、頁碼、貝茨編號（{page} {pages} {date} {file} {bates}）、浮水印\n'
            '                 --replace-existing：先去掉 Washi 先前加入的內容；washi pdf stamp <in.pdf> --remove headers|watermarks|all [-o out.pdf]：只去掉\n'
            '  washi pdf bookmarks <in.pdf> [--set outline.txt | --clear] [-o out.pdf]\n'
            '                                          列出書籤（每行一個：每層縮排兩個空格，定位字元，頁碼），\n'
            '                                          或用同樣格式的檔案寫出帶書籤的副本\n'
            '  washi pdf print <in.pdf> [--printer 名稱] [--copies 份數] [--pages 1-3,5] [--two-sided] [--gray] [--actual-size]\n'
            '  washi pdf printers                      列出印表機\n'
            '  washi pdf compress <in.pdf> [--level light|standard|strong] [-o out.pdf]   產生更小的副本（儲存前逐頁比對）\n'
            '  washi pdf flatten <in.pdf> [--fields-only | --comments-only] [-o out.pdf]   把欄位和註解畫進頁面（儲存前檢查）\n'
            '  washi pdf pdfa <in.pdf> [--check] [--flatten] [-o out.pdf]   用於長期保存的 PDF/A-2b 副本（--check 只列出需處理的地方）\n'
            '  washi pdf sign <in.pdf> --id id.p12 [--field 名稱 | --page n --box x0,y0,x1,y1] [--reason …] [--location …] [--tsa URL] [--certify …] [--ltv] [-o out.pdf]\n'
            '                                          以數位識別碼簽署（密碼取自 WASHI_ID_PASSWORD 或提示輸入）\n'
            '            認證簽章：--certify no-changes|forms|comments；--ltv 在簽署後加入長期驗證資訊\n'
            '  washi pdf verify <in.pdf> [--trust ca.cer]   核實檔案中的簽章\n'
            '  washi pdf ltv <in.pdf> [--tsa URL] [--trust ca.cer] [-o out.pdf]   為簽章加入長期驗證資訊（及文件時間戳記）\n'
            '  washi pdf labels <in.pdf> [--set 1:i,5:1 | --clear] [-o out.pdf]   頁碼標籤（檢視器顯示的頁碼）\n'
            '  washi pdf attachments <in.pdf> [--add a,b [--description …] | --remove a,b] [-o out.pdf] | --extract 資料夾 [--name a]\n'
            '  washi pdf layers <in.pdf> [--show a,b] [--hide c] [-o out.pdf]   列出圖層，或設定顯示\n'
            '  washi pdf protect <in.pdf> --to a.cer,b.cer [-o out.pdf]   用憑證加密；washi pdf unlock <in.pdf> --id id.p12   開啟\n'
            '  washi pdf data <in.pdf> [-o data.xfdf|.fdf|.csv|.json]   表單的值（寫入資料檔）\n'
            '  washi pdf data <in.pdf> --import data.xfdf [-o out.pdf]   |   --rows 表格.csv [-o 資料夾] [--name-column 欄]   每列一份副本\n'
            '  washi pdf collect <a.pdf> <b.pdf|資料夾> ... -o 表格.csv   將已填表單彙總為一個表格\n'
            '  washi pdf make-id --name "…" [--email …] -o id.p12   建立自簽署數位識別碼'
        ),
        "ko": (
            '페이지와 OCR:\n'
            '  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      파일 합치기 (입력란 유지)\n'
            '  washi pdf from-images <a.jpg> <b.png> ... [--page a4|letter|image] [--margin pt] [--dpi 150] -o out.pdf   사진과 스캔 이미지를 PDF 페이지로 (JPEG 는 그대로)\n'
            '  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n'
            '  washi pdf organize <in.pdf> [--pages 3,1-2,5] [--rotate 2:90,5:-90] -o out.pdf\n'
            '                                          페이지 순서 바꾸기, 삭제(나열하지 않은 페이지), 회전 (입력란 유지)\n'
            '  washi pdf crop <in.pdf> (--margins 위,오른쪽,아래,왼쪽 | --trim [--padding 6]) [--pages 1-3] [-o out.pdf]   여백 자르기(포인트) 또는 내용에 맞춰 자르기\n'
            '  washi pdf export-images <in.pdf> [--format png|jpeg] [--dpi 150] [--pages 1-3] [--quality 90] [-o 폴더]   페이지를 이미지 파일로\n'
            '  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n'
            '                                          스캔한 페이지를 검색 가능하게 (보이지 않는 텍스트 레이어 추가, 페이지 이미지는 그대로)\n'
            '  washi pdf erase <in.pdf> --box page:x0,y0,x1,y1 [--box ...] [-o out.pdf] [--all-ink] [--rules]\n'
            '                                          상자 안의 인쇄된 글자·이미지를 실제로 삭제 (포인트 단위, 왼쪽 위 원점; 색상 양식 인쇄는 유지)\n'
            '  washi pdf protect <in.pdf> -o out.pdf [--open] [--allow print,copy,edit,comment,fill,assemble|none]\n'
            '                                          열기 암호(--open)나 제한(--allow: 허용할 작업)을 추가, AES-256;\n'
            '                                          암호는 입력을 요청하거나 WASHI_OPEN_PASSWORD / WASHI_PERMISSIONS_PASSWORD 에서 읽습니다\n'
            '  washi pdf unlock <in.pdf> -o out.pdf   암호 해제 (입력 요청 또는 WASHI_PASSWORD)\n'
            '  washi pdf clean <in.pdf> [--list] [--only metadata,attachments,scripts,comments,private] [-o out.pdf]\n'
            '                                          숨은 정보 삭제 (--list 는 보여주기만)\n'
            '  washi pdf redact <in.pdf> [--find 단어 ...] [--pattern email,phone,number] [--regex 정규식] [--box page:x0,y0,x1,y1] [--case] [--whole-words] [--list] [-o out.pdf]\n'
            '                                          단어·개인정보·영역을 실제로 삭제하고 검게 가림 (--list 는 일치 항목만 표시)\n'
            '  washi pdf find <in.pdf> <텍스트> [--case] [--whole-words]   텍스트가 있는 곳을 페이지별로 표시\n'
            '  washi pdf links <in.pdf> [--find-addresses] [--add page:x0,y0,x1,y1=대상 ...] [--remove 1-2,3-1] [-o out.pdf]   링크 목록, 또는 추가(웹·메일 주소, 쪽 번호)와 삭제\n'
            '  washi pdf images <in.pdf> [--add page:x0,y0,x1,y1=photo.jpg ...] [--replace 1-2=logo.png ...] [--remove 3-1] [-o out.pdf]   페이지의 이미지 목록, 추가, 교체, 삭제\n'
            '  washi pdf compare <이전.pdf> <새.pdf> [-o marked.pdf]   바뀐 곳(글자, 페이지, 그림) 표시 (-o: 바뀐 곳마다 주석을 단 새 버전)\n'
            '  washi pdf replace-text <in.pdf> (--find 원래 텍스트 | --box page:x0,y0,x1,y1) --with 새 텍스트 [--page n] [--ignore-case] [--whole-words] [-o out.pdf]\n'
            '                                          페이지의 글자를 고칩니다 (한 줄씩, 저장 전에 확인)\n'
            '                 --paragraph "문단 안의 글자" --with "새 문단": 문단 전체를 원래 너비로 다시 조판\n'
            '  washi pdf stamp <in.pdf> [--header "왼쪽|가운데|오른쪽"] [--footer "…"] [--size 9] [--pages 1-3] [--bates 접두사 [--bates-start 1] [--bates-digits 6]]\n'
            '                 [--watermark 텍스트 [--opacity 0.3] [--angle 45] [--behind]] [-o out.pdf]   머리글·바닥글·쪽 번호·베이츠 번호({page} {pages} {date} {file} {bates}), 워터마크\n'
            '                 --replace-existing: Washi가 전에 넣은 것을 빼고 추가; washi pdf stamp <in.pdf> --remove headers|watermarks|all [-o out.pdf]: 빼기만\n'
            '  washi pdf bookmarks <in.pdf> [--set outline.txt | --clear] [-o out.pdf]\n'
            '                                          책갈피 목록 (한 줄에 하나: 수준마다 공백 두 칸, 탭, 페이지),\n'
            '                                          또는 같은 형식의 파일로 책갈피를 넣은 사본 작성\n'
            '  washi pdf print <in.pdf> [--printer 이름] [--copies 매수] [--pages 1-3,5] [--two-sided] [--gray] [--actual-size]\n'
            '  washi pdf printers                      프린터 목록\n'
            '  washi pdf compress <in.pdf> [--level light|standard|strong] [-o out.pdf]   더 작은 사본 (저장 전 페이지 비교)\n'
            '  washi pdf flatten <in.pdf> [--fields-only | --comments-only] [-o out.pdf]   입력란과 주석을 페이지에 그려 넣어 확정(저장 전 확인)\n'
            '  washi pdf pdfa <in.pdf> [--check] [--flatten] [-o out.pdf]   장기 보존용 PDF/A-2b 사본(--check는 고칠 점만 표시)\n'
            '  washi pdf sign <in.pdf> --id id.p12 [--field 이름 | --page n --box x0,y0,x1,y1] [--reason …] [--location …] [--tsa URL] [--certify …] [--ltv] [-o out.pdf]\n'
            '                                          디지털 ID 로 서명 (암호는 WASHI_ID_PASSWORD 또는 입력)\n'
            '            인증 서명: --certify no-changes|forms|comments, --ltv는 서명 후 장기 검증 정보 추가\n'
            '  washi pdf verify <in.pdf> [--trust ca.cer]   파일의 서명 확인\n'
            '  washi pdf ltv <in.pdf> [--tsa URL] [--trust ca.cer] [-o out.pdf]   서명에 장기 검증 정보(와 문서 타임스탬프) 추가\n'
            '  washi pdf labels <in.pdf> [--set 1:i,5:1 | --clear] [-o out.pdf]   페이지 레이블(뷰어의 쪽 번호)\n'
            '  washi pdf attachments <in.pdf> [--add a,b [--description …] | --remove a,b] [-o out.pdf] | --extract 폴더 [--name a]\n'
            '  washi pdf layers <in.pdf> [--show a,b] [--hide c] [-o out.pdf]   레이어 목록, 표시 설정\n'
            '  washi pdf protect <in.pdf> --to a.cer,b.cer [-o out.pdf]   인증서로 암호화; washi pdf unlock <in.pdf> --id id.p12   열기\n'
            '  washi pdf data <in.pdf> [-o data.xfdf|.fdf|.csv|.json]   양식의 값(데이터 파일로)\n'
            '  washi pdf data <in.pdf> --import data.xfdf [-o out.pdf]   |   --rows 표.csv [-o 폴더] [--name-column 열]   행마다 사본\n'
            '  washi pdf collect <a.pdf> <b.pdf|폴더> ... -o 표.csv   작성된 양식을 하나의 표로\n'
            '  washi pdf make-id --name "…" [--email …] -o id.p12   자체 서명 디지털 ID 만들기'
        ),
        "fr": (
            'Pages et OCR :\n'
            '  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      assemble des fichiers (les champs de formulaire sont conservés)\n'
            '  washi pdf from-images <a.jpg> <b.png> ... [--page a4|letter|image] [--margin pt] [--dpi 150] -o out.pdf   photos et numérisations en pages (JPEG gardé tel quel)\n'
            '  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dossier]\n'
            '  washi pdf organize <in.pdf> [--pages 3,1-2,5] [--rotate 2:90,5:-90] -o out.pdf\n'
            '                                          réordonne, retire (pages non listées) et fait pivoter les pages (les champs de formulaire sont conservés)\n'
            '  washi pdf crop <in.pdf> (--margins haut,droite,bas,gauche | --trim [--padding 6]) [--pages 1-3] [-o out.pdf]   rogner les marges (points) ou au contenu\n'
            '  washi pdf export-images <in.pdf> [--format png|jpeg] [--dpi 150] [--pages 1-3] [--quality 90] [-o dossier]   pages en fichiers image\n'
            '  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n'
            '                                          rend les pages numérisées cherchables (couche de texte invisible ; l’image de la page ne change pas)\n'
            '  washi pdf erase <in.pdf> --box page:x0,y0,x1,y1 [--box ...] [-o out.pdf] [--all-ink] [--rules]\n'
            '                                          supprime pour de bon le texte et les images imprimés dans les cadres (points, origine en haut à gauche ; l’impression en couleur du formulaire reste)\n'
            '  washi pdf protect <in.pdf> -o out.pdf [--open] [--allow print,copy,edit,comment,fill,assemble|none]\n'
            '                                          ajoute un mot de passe d’ouverture (--open) et/ou des limites (--allow : ce qui reste permis), AES-256 ;\n'
            '                                          les mots de passe sont demandés, ou lus dans WASHI_OPEN_PASSWORD / WASHI_PERMISSIONS_PASSWORD\n'
            '  washi pdf unlock <in.pdf> -o out.pdf   retire le mot de passe (demandé, ou WASHI_PASSWORD)\n'
            '  washi pdf clean <in.pdf> [--list] [--only metadata,attachments,scripts,comments,private] [-o out.pdf]\n'
            '                                          supprime les informations masquées (--list les affiche seulement)\n'
            '  washi pdf redact <in.pdf> [--find mot ...] [--pattern email,phone,number] [--regex RE] [--box page:x0,y0,x1,y1] [--case] [--whole-words] [--list] [-o out.pdf]\n'
            '                                          supprime pour de bon des mots, des données personnelles ou des zones et les couvre de noir (--list affiche seulement les occurrences)\n'
            '  washi pdf find <in.pdf> <texte> [--case] [--whole-words]   où se trouve le texte, page par page\n'
            '  washi pdf links <in.pdf> [--find-addresses] [--add page:x0,y0,x1,y1=cible ...] [--remove 1-2,3-1] [-o out.pdf]   lister les liens, ou en ajouter (adresse web/mail, page) et en retirer\n'
            '  washi pdf images <in.pdf> [--add page:x0,y0,x1,y1=photo.jpg ...] [--replace 1-2=logo.png ...] [--remove 3-1] [-o out.pdf]   lister, ajouter, remplacer ou retirer les images des pages\n'
            '  washi pdf compare <ancien.pdf> <nouveau.pdf> [-o marked.pdf]   ce qui a changé : mots, pages, images (-o : la nouvelle version annotée)\n'
            '  washi pdf replace-text <in.pdf> (--find ancien | --box page:x0,y0,x1,y1) --with nouveau [--page n] [--ignore-case] [--whole-words] [-o out.pdf]\n'
            '                                          modifier le texte de la page (une ligne à la fois ; vérifié avant d’être gardé)\n'
            '                 --paragraph "texte du paragraphe" --with "nouveau paragraphe" : recomposer tout le paragraphe dans sa largeur\n'
            '  washi pdf stamp <in.pdf> [--header "gauche|centre|droite"] [--footer "…"] [--size 9] [--pages 1-3] [--bates PRÉFIXE [--bates-start 1] [--bates-digits 6]]\n'
            '                 [--watermark TEXTE [--opacity 0.3] [--angle 45] [--behind]] [-o out.pdf]   en-têtes, pieds de page, numéros de page et Bates ({page} {pages} {date} {file} {bates}), filigrane\n'
            '                 --replace-existing : retire d’abord ce que Washi avait ajouté ; washi pdf stamp <in.pdf> --remove headers|watermarks|all [-o out.pdf] : seulement le retirer\n'
            '  washi pdf bookmarks <in.pdf> [--set outline.txt | --clear] [-o out.pdf]\n'
            '                                          liste les signets (un par ligne : deux espaces par niveau, une tabulation, la page),\n'
            '                                          ou écrit une copie avec ceux d’un fichier de cette forme\n'
            '  washi pdf print <in.pdf> [--printer nom] [--copies n] [--pages 1-3,5] [--two-sided] [--gray] [--actual-size]\n'
            '  washi pdf printers                      liste les imprimantes\n'
            '  washi pdf compress <in.pdf> [--level light|standard|strong] [-o out.pdf]   une copie plus légère (pages comparées avant d’être gardée)\n'
            '  washi pdf flatten <in.pdf> [--fields-only | --comments-only] [-o out.pdf]   dessiner champs et commentaires dans la page (vérifié avant d’être conservé)\n'
            '  washi pdf pdfa <in.pdf> [--check] [--flatten] [-o out.pdf]   une copie PDF/A-2b pour l’archivage (--check liste seulement ce qui fait obstacle)\n'
            '  washi pdf sign <in.pdf> --id id.p12 [--field nom | --page n --box x0,y0,x1,y1] [--reason …] [--location …] [--tsa URL] [--certify …] [--ltv] [-o out.pdf]\n'
            '                                          signe avec une identité numérique (mot de passe : WASHI_ID_PASSWORD ou demandé)\n'
            '            certifier : --certify no-changes|forms|comments ; --ltv ajoute la validation à long terme après la signature\n'
            '  washi pdf verify <in.pdf> [--trust ca.cer]   vérifie les signatures d’un fichier\n'
            '  washi pdf ltv <in.pdf> [--tsa URL] [--trust ca.cer] [-o out.pdf]   ajoute la validation à long terme (et un horodatage du document) aux signatures\n'
            '  washi pdf labels <in.pdf> [--set 1:i,5:1 | --clear] [-o out.pdf]   étiquettes de page (numéros affichés)\n'
            '  washi pdf attachments <in.pdf> [--add a,b [--description …] | --remove a,b] [-o out.pdf] | --extract dossier [--name a]\n'
            '  washi pdf layers <in.pdf> [--show a,b] [--hide c] [-o out.pdf]   liste les calques, ou règle ceux affichés\n'
            '  washi pdf protect <in.pdf> --to a.cer,b.cer [-o out.pdf]   chiffrer pour des certificats ; washi pdf unlock <in.pdf> --id id.p12   l’ouvrir\n'
            '  washi pdf data <in.pdf> [-o data.xfdf|.fdf|.csv|.json]   les valeurs du formulaire (vers un fichier)\n'
            '  washi pdf data <in.pdf> --import data.xfdf [-o out.pdf]   |   --rows tableau.csv [-o dossier] [--name-column col]   une copie par ligne\n'
            '  washi pdf collect <a.pdf> <b.pdf|dossier> ... -o tableau.csv   des formulaires remplis en un tableau\n'
            '  washi pdf make-id --name "…" [--email …] -o id.p12   crée une identité auto-signée'
        ),
        "de": (
            'Seiten und OCR:\n'
            '  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      Dateien zusammenfügen (Formularfelder bleiben erhalten)\n'
            '  washi pdf from-images <a.jpg> <b.png> ... [--page a4|letter|image] [--margin pt] [--dpi 150] -o out.pdf   Fotos und Scans als Seiten (JPEG bleibt, wie es ist)\n'
            '  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o Ordner]\n'
            '  washi pdf organize <in.pdf> [--pages 3,1-2,5] [--rotate 2:90,5:-90] -o out.pdf\n'
            '                                          Seiten umordnen, entfernen (nicht aufgeführte) und drehen (Formularfelder bleiben erhalten)\n'
            '  washi pdf crop <in.pdf> (--margins oben,rechts,unten,links | --trim [--padding 6]) [--pages 1-3] [-o out.pdf]   Ränder abschneiden (Punkt) oder auf den Inhalt zuschneiden\n'
            '  washi pdf export-images <in.pdf> [--format png|jpeg] [--dpi 150] [--pages 1-3] [--quality 90] [-o Ordner]   Seiten als Bilddateien\n'
            '  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n'
            '                                          gescannte Seiten durchsuchbar machen (unsichtbare Textebene; das Seitenbild bleibt unverändert)\n'
            '  washi pdf erase <in.pdf> --box page:x0,y0,x1,y1 [--box ...] [-o out.pdf] [--all-ink] [--rules]\n'
            '                                          gedruckten Text und Bilder in Rahmen wirklich entfernen (Punkte, Ursprung oben links; farbiger Formulardruck bleibt)\n'
            '  washi pdf protect <in.pdf> -o out.pdf [--open] [--allow print,copy,edit,comment,fill,assemble|none]\n'
            '                                          Öffnungspasswort (--open) und/oder Einschränkungen (--allow: was erlaubt bleibt) hinzufügen, AES-256;\n'
            '                                          Passwörter werden abgefragt oder aus WASHI_OPEN_PASSWORD / WASHI_PERMISSIONS_PASSWORD gelesen\n'
            '  washi pdf unlock <in.pdf> -o out.pdf   Passwort entfernen (abgefragt, oder WASHI_PASSWORD)\n'
            '  washi pdf clean <in.pdf> [--list] [--only metadata,attachments,scripts,comments,private] [-o out.pdf]\n'
            '                                          versteckte Informationen entfernen (--list zeigt sie nur)\n'
            '  washi pdf redact <in.pdf> [--find Wort ...] [--pattern email,phone,number] [--regex RE] [--box page:x0,y0,x1,y1] [--case] [--whole-words] [--list] [-o out.pdf]\n'
            '                                          Wörter, persönliche Daten oder Bereiche wirklich entfernen und schwarz abdecken (--list zeigt nur die Treffer)\n'
            '  washi pdf find <in.pdf> <Text> [--case] [--whole-words]   wo der Text steht, Seite für Seite\n'
            '  washi pdf links <in.pdf> [--find-addresses] [--add page:x0,y0,x1,y1=Ziel ...] [--remove 1-2,3-1] [-o out.pdf]   Links auflisten, oder hinzufügen (Web-/Mailadresse, Seite) und entfernen\n'
            '  washi pdf images <in.pdf> [--add page:x0,y0,x1,y1=photo.jpg ...] [--replace 1-2=logo.png ...] [--remove 3-1] [-o out.pdf]   Bilder auf den Seiten auflisten, hinzufügen, ersetzen oder entfernen\n'
            '  washi pdf compare <alt.pdf> <neu.pdf> [-o marked.pdf]   was sich geändert hat: Wörter, Seiten, Bilder (-o: die neue Version mit markierten Änderungen)\n'
            '  washi pdf replace-text <in.pdf> (--find alt | --box page:x0,y0,x1,y1) --with neu [--page n] [--ignore-case] [--whole-words] [-o out.pdf]\n'
            '                                          Text auf der Seite ändern (eine Zeile auf einmal; vor dem Behalten geprüft)\n'
            '                 --paragraph "Text darin" --with "neuer Absatz": den ganzen Absatz in seiner Breite neu setzen\n'
            '  washi pdf stamp <in.pdf> [--header "links|Mitte|rechts"] [--footer "…"] [--size 9] [--pages 1-3] [--bates PRÄFIX [--bates-start 1] [--bates-digits 6]]\n'
            '                 [--watermark TEXT [--opacity 0.3] [--angle 45] [--behind]] [-o out.pdf]   Kopf- und Fußzeilen, Seiten- und Bates-Nummern ({page} {pages} {date} {file} {bates}), Wasserzeichen\n'
            '                 --replace-existing: zuerst entfernen, was Washi früher hinzugefügt hat; washi pdf stamp <in.pdf> --remove headers|watermarks|all [-o out.pdf]: nur entfernen\n'
            '  washi pdf bookmarks <in.pdf> [--set outline.txt | --clear] [-o out.pdf]\n'
            '                                          Lesezeichen auflisten (eins pro Zeile: zwei Leerzeichen pro Ebene, Tab, Seite)\n'
            '                                          oder eine Kopie mit denen aus einer solchen Datei schreiben\n'
            '  washi pdf print <in.pdf> [--printer Name] [--copies n] [--pages 1-3,5] [--two-sided] [--gray] [--actual-size]\n'
            '  washi pdf printers                      Drucker auflisten\n'
            '  washi pdf compress <in.pdf> [--level light|standard|strong] [-o out.pdf]   eine kleinere Kopie (Seiten vor dem Speichern verglichen)\n'
            '  washi pdf flatten <in.pdf> [--fields-only | --comments-only] [-o out.pdf]   Felder und Kommentare in die Seite zeichnen (vor dem Speichern geprüft)\n'
            '  washi pdf pdfa <in.pdf> [--check] [--flatten] [-o out.pdf]   eine PDF/A-2b-Kopie zum Archivieren (--check listet nur, was im Weg steht)\n'
            '  washi pdf sign <in.pdf> --id id.p12 [--field Name | --page n --box x0,y0,x1,y1] [--reason …] [--location …] [--tsa URL] [--certify …] [--ltv] [-o out.pdf]\n'
            '                                          mit digitaler ID signieren (Passwort aus WASHI_ID_PASSWORD oder abgefragt)\n'
            '            zertifizieren: --certify no-changes|forms|comments; --ltv fügt nach dem Signieren Langzeitvalidierung hinzu\n'
            '  washi pdf verify <in.pdf> [--trust ca.cer]   Signaturen einer Datei prüfen\n'
            '  washi pdf ltv <in.pdf> [--tsa URL] [--trust ca.cer] [-o out.pdf]   Langzeitvalidierung (und Dokumentzeitstempel) zu den Signaturen hinzufügen\n'
            '  washi pdf labels <in.pdf> [--set 1:i,5:1 | --clear] [-o out.pdf]   Seitenbeschriftung (angezeigte Seitenzahlen)\n'
            '  washi pdf attachments <in.pdf> [--add a,b [--description …] | --remove a,b] [-o out.pdf] | --extract Ordner [--name a]\n'
            '  washi pdf layers <in.pdf> [--show a,b] [--hide c] [-o out.pdf]   Ebenen auflisten oder einstellen, welche sichtbar sind\n'
            '  washi pdf protect <in.pdf> --to a.cer,b.cer [-o out.pdf]   für Zertifikate verschlüsseln; washi pdf unlock <in.pdf> --id id.p12   öffnen\n'
            '  washi pdf data <in.pdf> [-o data.xfdf|.fdf|.csv|.json]   die Werte des Formulars (in eine Datei)\n'
            '  washi pdf data <in.pdf> --import data.xfdf [-o out.pdf]   |   --rows tabelle.csv [-o Ordner] [--name-column Spalte]   eine Kopie je Zeile\n'
            '  washi pdf collect <a.pdf> <b.pdf|Ordner> ... -o tabelle.csv   ausgefüllte Formulare in eine Tabelle\n'
            '  washi pdf make-id --name "…" [--email …] -o id.p12   selbstsignierte digitale ID erstellen'
        ),
    },
    "Pdf_Erased": {
        "en": "Saved {0}: removed {1} text run(s), {2} image(s), {3} drawing(s). Check: {4} character(s) left in the erased areas, {5} pixel(s) changed elsewhere.",
        "ja": "{0} を保存しました: 文字 {1}、画像 {2}、図形 {3} を削除。確認: 消去した場所に残った文字 {4}、それ以外で変わった画素 {5}。",
        "zh-Hans": "已保存 {0}：删除了文字 {1} 段、图像 {2} 个、图形 {3} 个。检查：删除处剩余文字 {4} 个，其他地方变化的像素 {5} 个。",
        "zh-Hant": "已儲存 {0}：刪除了文字 {1} 段、影像 {2} 個、圖形 {3} 個。檢查：刪除處剩餘文字 {4} 個，其他地方變化的像素 {5} 個。",
        "ko": "{0} 저장: 글자 {1}개, 이미지 {2}개, 도형 {3}개 삭제. 확인: 지운 곳에 남은 글자 {4}개, 다른 곳에서 바뀐 픽셀 {5}개.",
        "fr": "{0} enregistré : {1} texte(s), {2} image(s), {3} dessin(s) supprimés. Contrôle : {4} caractère(s) restant(s) dans les zones effacées, {5} pixel(s) modifié(s) ailleurs.",
        "de": "{0} gespeichert: {1} Text(e), {2} Bild(er), {3} Zeichnung(en) entfernt. Prüfung: {4} Zeichen in den gelöschten Bereichen übrig, {5} Pixel anderswo geändert.",
    },
    "Pdf_EraseKept": {
        "en": "  kept on page {0}: {1}", "ja": "  {0} ページで残したもの: {1}", "zh-Hans": "  第 {0} 页保留：{1}", "zh-Hant": "  第 {0} 頁保留：{1}",
        "ko": "  {0}쪽에서 유지: {1}", "fr": "  conservé page {0} : {1}", "de": "  auf Seite {0} behalten: {1}",
    },
    "Pdf_Merged": {
        "en": "{0} file(s) merged into {1}: {2} page(s), {3} form field(s)",
        "ja": "{0} 個のファイルを {1} に結合しました: {2} ページ、記入欄 {3} 個",
        "zh-Hans": "已将 {0} 个文件合并为 {1}：{2} 页，{3} 个表单栏位",
        "zh-Hant": "已將 {0} 個檔案合併為 {1}：{2} 頁，{3} 個表單欄位",
        "ko": "파일 {0}개를 {1}(으)로 합쳤습니다: {2}페이지, 입력란 {3}개",
        "fr": "{0} fichier(s) assemblé(s) dans {1} : {2} page(s), {3} champ(s) de formulaire",
        "de": "{0} Datei(en) zu {1} zusammengefügt: {2} Seite(n), {3} Formularfeld(er)",
    },
    "Pdf_Organized": {
        "en": "{0} written: {1} page(s), {2} form field(s)",
        "ja": "{0} を書き出しました: {1} ページ、記入欄 {2} 個",
        "zh-Hans": "已写入 {0}：{1} 页，{2} 个表单栏位",
        "zh-Hant": "已寫入 {0}：{1} 頁，{2} 個表單欄位",
        "ko": "{0} 저장: {1}페이지, 입력란 {2}개",
        "fr": "{0} écrit : {1} page(s), {2} champ(s) de formulaire",
        "de": "{0} geschrieben: {1} Seite(n), {2} Formularfeld(er)",
    },
    "Pdf_SplitPart": {
        "en": "  {0}  (pages {1}-{2})",
        "ja": "  {0}  （{1}〜{2} ページ）",
        "zh-Hans": "  {0}  （第 {1}-{2} 页）",
        "zh-Hant": "  {0}  （第 {1}-{2} 頁）",
        "ko": "  {0}  ({1}-{2}페이지)",
        "fr": "  {0}  (pages {1} à {2})",
        "de": "  {0}  (Seiten {1}-{2})",
    },
    "Pdf_Failed": {
        "en": "washi: {0}",
        "ja": "washi: {0}",
        "zh-Hans": "washi：{0}",
        "zh-Hant": "washi：{0}",
        "ko": "washi: {0}",
        "fr": "washi : {0}",
        "de": "washi: {0}",
    },
    "Ocr_Engine": {
        "en": "OCR with {0}: {1} page(s) to recognise",
        "ja": "{0} で OCR: 認識するページ {1}",
        "zh-Hans": "使用 {0} 进行 OCR：需识别 {1} 页",
        "zh-Hant": "使用 {0} 進行 OCR：需辨識 {1} 頁",
        "ko": "{0}(으)로 OCR: 인식할 페이지 {1}개",
        "fr": "OCR avec {0} : {1} page(s) à reconnaître",
        "de": "OCR mit {0}: {1} Seite(n) zu erkennen",
    },
    "Ocr_Progress": {
        "en": "  page {0} of {1} done",
        "ja": "  {1} ページ中 {0} ページ完了",
        "zh-Hans": "  已完成 {0}/{1} 页",
        "zh-Hant": "  已完成 {0}/{1} 頁",
        "ko": "  {1}페이지 중 {0}페이지 완료",
        "fr": "  page {0} sur {1} terminée",
        "de": "  Seite {0} von {1} fertig",
    },
    "Ocr_Done": {
        "en": "{0} page(s) made searchable: {1}",
        "ja": "{0} ページを検索できるようにしました: {1}",
        "zh-Hans": "已让 {0} 页可以搜索：{1}",
        "zh-Hant": "已讓 {0} 頁可以搜尋：{1}",
        "ko": "{0}페이지를 검색 가능하게 만들었습니다: {1}",
        "fr": "{0} page(s) rendue(s) cherchable(s) : {1}",
        "de": "{0} Seite(n) durchsuchbar gemacht: {1}",
    },
    "Ocr_Nothing": {
        "en": "No page needs OCR: every page already has text or a Washi text layer (use --force to recognise pages with text anyway).",
        "ja": "OCR が必要なページはありません。どのページにも文字情報か Washi の文字層があります（それでも認識するには --force）。",
        "zh-Hans": "没有需要 OCR 的页面：每页都已有文字或 Washi 文字层（如仍要识别，请加 --force）。",
        "zh-Hant": "沒有需要 OCR 的頁面：每頁都已有文字或 Washi 文字層（如仍要辨識，請加 --force）。",
        "ko": "OCR 이 필요한 페이지가 없습니다. 모든 페이지에 이미 텍스트나 Washi 텍스트 레이어가 있습니다 (그래도 인식하려면 --force).",
        "fr": "Aucune page n’a besoin d’OCR : chaque page a déjà du texte ou une couche de texte Washi (--force pour reconnaître quand même).",
        "de": "Keine Seite braucht OCR: Jede Seite hat bereits Text oder eine Washi-Textebene (--force, um trotzdem zu erkennen).",
    },
    "Ocr_TextWritten": {
        "en": "recognised text: {0}",
        "ja": "認識した文字: {0}",
        "zh-Hans": "识别出的文字：{0}",
        "zh-Hant": "辨識出的文字：{0}",
        "ko": "인식한 텍스트: {0}",
        "fr": "texte reconnu : {0}",
        "de": "erkannter Text: {0}",
    },
    "Ocr_MissingLangs": {
        "en": "tesseract: these languages are not installed and were skipped: {0} (macOS: brew install tesseract-lang)",
        "ja": "tesseract: 次の言語はインストールされていないため使いませんでした: {0}（macOS: brew install tesseract-lang）",
        "zh-Hans": "tesseract：以下语言未安装，已跳过：{0}（macOS：brew install tesseract-lang）",
        "zh-Hant": "tesseract：以下語言未安裝，已略過：{0}（macOS：brew install tesseract-lang）",
        "ko": "tesseract: 다음 언어가 설치되어 있지 않아 건너뛰었습니다: {0} (macOS: brew install tesseract-lang)",
        "fr": "tesseract : ces langues ne sont pas installées et ont été ignorées : {0} (macOS : brew install tesseract-lang)",
        "de": "tesseract: Diese Sprachen sind nicht installiert und wurden übersprungen: {0} (macOS: brew install tesseract-lang)",
    },
}

# ---------------------------------------------------------------- editor layout (tools, panels)
# order: en, ja, zh-Hans, zh-Hant, ko, fr, de
_UI = {
    "Ui_Settings": ["Settings", "設定", "设置", "設定", "설정", "Réglages", "Einstellungen"],
    "Ui_SettingsMenu": ["Settings…", "設定…", "设置…", "設定…", "설정…", "Réglages…", "Einstellungen…"],
    "Ui_General": ["General", "一般", "通用", "一般", "일반", "Général", "Allgemein"],
    "Ui_About": ["About Washi", "Washi について", "关于 Washi", "關於 Washi", "Washi 정보", "À propos de Washi", "Über Washi"],
    "Ui_LanguageCaption": ["LANGUAGE", "表示言語", "界面语言", "介面語言", "표시 언어", "LANGUE", "SPRACHE"],
    "Ui_LangSystem": ["System language", "システムの言語", "跟随系统", "跟隨系統", "시스템 언어", "Langue du système", "Systemsprache"],
    "Ui_LanguageNote": ["The window reopens in the new language; an open document stays open.",
                        "ウィンドウが新しい言語で開き直します。開いている文書はそのままです。",
                        "窗口会以新语言重新打开，已打开的文件保持打开。",
                        "視窗會以新語言重新開啟，已開啟的檔案保持開啟。",
                        "창이 새 언어로 다시 열립니다. 열려 있는 문서는 그대로 유지됩니다.",
                        "La fenêtre se rouvre dans la nouvelle langue ; le document ouvert le reste.",
                        "Das Fenster öffnet sich in der neuen Sprache neu; ein offenes Dokument bleibt offen."],
    "Ui_AiModelNote": ["The model behind the assistant, field naming, form filling and AI OCR. API keys are kept in the macOS Keychain, never in a file.",
                       "アシスタント、記入欄の名前付け、フォーム記入、AI OCR に使うモデルです。API キーは macOS のキーチェーンに保存され、ファイルには書かれません。",
                       "助手、填写栏命名、表单填写和 AI OCR 使用的模型。API key 保存在 macOS 钥匙串中，不会写入文件。",
                       "助理、填寫欄命名、表單填寫和 AI OCR 使用的模型。API key 儲存在 macOS 鑰匙圈中，不會寫入檔案。",
                       "어시스턴트, 입력란 이름 붙이기, 양식 작성, AI OCR 에 쓰는 모델입니다. API 키는 macOS 키체인에 보관되며 파일에는 저장되지 않습니다.",
                       "Le modèle de l’assistant, du nommage des champs, du remplissage et de l’OCR par IA. Les clés d’API restent dans le trousseau macOS, jamais dans un fichier.",
                       "Das Modell für den Assistenten, die Feldbenennung, das Ausfüllen und die KI-OCR. API-Schlüssel liegen im macOS-Schlüsselbund, nie in einer Datei."],
    "Ui_Version": ["Version {0}", "バージョン {0}", "版本 {0}", "版本 {0}", "버전 {0}", "Version {0}", "Version {0}"],
    "Ui_Tagline": ["Open-source PDF toolkit for desktop, command line and AI agents: fillable forms, OCR, combine and split, built-in AI and an MCP server.",
                   "デスクトップ、コマンドライン、AI エージェントのためのオープンソース PDF ツールキット。記入できるフォーム、OCR、結合と分割、内蔵 AI、MCP サーバー。",
                   "面向桌面、命令行和 AI 智能体的开源 PDF 工具：可填写表单、OCR、合并与拆分、内置 AI 和 MCP 服务器。",
                   "面向桌面、命令列和 AI 代理的開源 PDF 工具：可填寫表單、OCR、合併與分割、內建 AI 和 MCP 伺服器。",
                   "데스크톱, 명령줄, AI 에이전트를 위한 오픈 소스 PDF 도구: 입력 가능한 양식, OCR, 합치기와 나누기, 내장 AI, MCP 서버.",
                   "Boîte à outils PDF open source pour le bureau, la ligne de commande et les agents d’IA : formulaires remplissables, OCR, assemblage et division, IA intégrée et serveur MCP.",
                   "Open-Source-PDF-Werkzeug für Desktop, Kommandozeile und KI-Agenten: ausfüllbare Formulare, OCR, Zusammenfügen und Teilen, eingebaute KI und ein MCP-Server."],
    "Ui_LicenseCaption": ["LICENSE", "ライセンス", "许可证", "授權條款", "라이선스", "LICENCE", "LIZENZ"],
    "Ui_LicenseText": ["Washi is free software: you may use, study, share and change it. If you share a changed version, or run it for others over a network, you must offer its source code under the same license.",
                       "Washi はフリーソフトウェアです。自由に使い、調べ、共有し、変更できます。変更版を配布する場合や、ネットワーク越しに他の人へ提供する場合は、同じライセンスでソースコードを公開する必要があります。",
                       "Washi 是自由软件：你可以使用、研究、分享和修改它。如果你分发修改后的版本，或通过网络向他人提供服务，就必须以相同许可证公开其源代码。",
                       "Washi 是自由軟體：你可以使用、研究、分享和修改它。如果你散布修改後的版本，或透過網路向他人提供服務，就必須以相同授權公開其原始碼。",
                       "Washi 는 자유 소프트웨어입니다. 사용, 연구, 공유, 수정할 수 있습니다. 수정한 버전을 배포하거나 네트워크로 다른 사람에게 제공하면 같은 라이선스로 소스 코드를 공개해야 합니다.",
                       "Washi est un logiciel libre : vous pouvez l’utiliser, l’étudier, le partager et le modifier. Si vous diffusez une version modifiée, ou la faites tourner pour d’autres sur un réseau, vous devez en offrir le code source sous la même licence.",
                       "Washi ist freie Software: Sie dürfen sie nutzen, untersuchen, weitergeben und ändern. Wer eine geänderte Fassung weitergibt oder sie für andere über ein Netzwerk betreibt, muss den Quellcode unter derselben Lizenz anbieten."],
    "Ui_SourceCode": ["Source code", "ソースコード", "源代码", "原始碼", "소스 코드", "Code source", "Quellcode"],
    "Ui_ReadLicense": ["Read the license", "ライセンスを読む", "阅读许可证", "閱讀授權條款", "라이선스 읽기", "Lire la licence", "Lizenz lesen"],
    "Ui_ThirdParty": ["OPEN-SOURCE COMPONENTS", "オープンソースコンポーネント", "开源组件", "開源元件", "오픈 소스 구성 요소", "COMPOSANTS OPEN SOURCE", "OPEN-SOURCE-KOMPONENTEN"],
    "Ui_ThirdPartyNote": ["Washi is built on these projects, each under its own license. Thank you to their authors.",
                          "Washi は次のプロジェクトの上に作られています。それぞれ独自のライセンスです。作者の皆さんに感謝します。",
                          "Washi 构建于以下项目之上，各自采用其许可证。感谢它们的作者。",
                          "Washi 建構於以下專案之上，各自採用其授權。感謝它們的作者。",
                          "Washi 는 다음 프로젝트 위에 만들어졌으며 각각 고유한 라이선스를 따릅니다. 작성자분들께 감사드립니다.",
                          "Washi repose sur ces projets, chacun sous sa propre licence. Merci à leurs auteurs.",
                          "Washi baut auf diesen Projekten auf, jedes unter seiner eigenen Lizenz. Danke an ihre Autorinnen und Autoren."],
    "Ui_NotBundled": ["optional, installed separately", "任意。別途インストール", "可选，需另行安装", "選用，需另行安裝", "선택 사항, 별도 설치", "facultatif, installé à part", "optional, separat installiert"],
    "Ui_PreviewTip": ["Preview: try filling the form (P)", "プレビュー: 実際に記入してみる（P）", "预览：试着填写表单（P）", "預覽：試著填寫表單（P）", "미리 보기: 양식을 직접 채워 보기 (P)", "Aperçu : essayer de remplir (P)", "Vorschau: Formular ausprobieren (P)"],
    "Ui_PreviewHint": ["Click a field to type, a box to tick it. Tab moves on, Delete clears. What you type is saved into the PDF you make.",
                       "記入欄をクリックして入力、チェックボックスはクリックでチェック。Tab で次へ、Delete で消去。入力した内容は作成する PDF に保存されます。",
                       "点击栏位输入，点击复选框打勾。Tab 到下一个，Delete 清除。输入的内容会保存到生成的 PDF 中。",
                       "點擊欄位輸入，點擊核取方塊打勾。Tab 到下一個，Delete 清除。輸入的內容會儲存到產生的 PDF 中。",
                       "입력란을 클릭해 입력하고 체크박스는 클릭해 체크합니다. Tab 은 다음, Delete 는 지우기. 입력한 내용은 만드는 PDF 에 저장됩니다.",
                       "Cliquez sur un champ pour saisir, sur une case pour la cocher. Tab passe au suivant, Suppr efface. La saisie est enregistrée dans le PDF créé.",
                       "Feld anklicken zum Tippen, Kästchen anklicken zum Ankreuzen. Tab weiter, Entf löscht. Die Eingaben kommen in das erstellte PDF."],
    "Ui_MadeFilled": ["Saved {0} with {1} filled field(s).", "{0} を保存しました（記入済み {1} 欄）。", "已保存 {0}（已填写 {1} 个栏位）。", "已儲存 {0}（已填寫 {1} 個欄位）。", "{0} 을(를) 저장했습니다 (채운 입력란 {1}개).", "{0} enregistré, {1} champ(s) rempli(s).", "{0} gespeichert, {1} Feld(er) ausgefüllt."],
    "Ui_MenuFile": ["File", "ファイル", "文件", "檔案", "파일", "Fichier", "Datei"],
    "Ui_MenuView": ["View", "表示", "视图", "顯示", "보기", "Présentation", "Darstellung"],
    "Ui_Close": ["Close", "閉じる", "关闭", "關閉", "닫기", "Fermer", "Schließen"],
    "Ui_ZoomIn": ["Zoom in", "拡大", "放大", "放大", "확대", "Zoom avant", "Vergrößern"],
    "Ui_ZoomOut": ["Zoom out", "縮小", "缩小", "縮小", "축소", "Zoom arrière", "Verkleinern"],
    "Ui_FitWidth": ["Fit width", "幅に合わせる", "适合宽度", "符合寬度", "너비에 맞춤", "Ajuster à la largeur", "An Breite anpassen"],
    "Ui_FitPage": ["Fit page", "ページ全体", "适合页面", "符合頁面", "페이지에 맞춤", "Page entière", "Ganze Seite"],
    "Ui_PrevPage": ["Previous page (Page Up)", "前のページ（Page Up）", "上一页（Page Up）", "上一頁（Page Up）", "이전 페이지 (Page Up)", "Page précédente (Pg préc.)", "Vorherige Seite (Bild ↑)"],
    "Ui_NextPage": ["Next page (Page Down)", "次のページ（Page Down）", "下一页（Page Down）", "下一頁（Page Down）", "다음 페이지 (Page Down)", "Page suivante (Pg suiv.)", "Nächste Seite (Bild ↓)"],
    "Ui_ToolForm": ["Form", "記入欄", "表单", "表單", "양식", "Formulaire", "Formular"],
    "Ui_ToolOcr": ["OCR", "OCR", "OCR", "OCR", "OCR", "OCR", "OCR"],
    "Ui_ToolCombine": ["Combine", "結合", "合并", "合併", "합치기", "Assembler", "Kombinieren"],
    "Ui_ToolSplit": ["Split", "分割", "拆分", "分割", "나누기", "Diviser", "Teilen"],
    "Ui_ToolAsk": ["AI assistant", "AI アシスタント", "AI 助手", "AI 助理", "AI 어시스턴트", "Assistant IA", "KI-Assistent"],
    "Ui_FormTitle": ["Prepare a form", "記入欄を準備", "准备表单", "準備表單", "양식 준비", "Préparer un formulaire", "Formular vorbereiten"],
    "Ui_FormLead": ["Washi finds the boxes to fill in. Check them, fix what is wrong, then save a fillable PDF.",
                    "Washi が記入欄を見つけます。確認して直し、記入できる PDF として保存します。",
                    "Washi 会找出要填写的栏位。检查并修正后，保存为可填写的 PDF。",
                    "Washi 會找出要填寫的欄位。檢查並修正後，儲存為可填寫的 PDF。",
                    "Washi 가 입력란을 찾습니다. 확인하고 고친 다음 입력 가능한 PDF 로 저장하세요.",
                    "Washi trouve les cases à remplir. Vérifiez-les, corrigez, puis enregistrez un PDF remplissable.",
                    "Washi findet die auszufüllenden Felder. Prüfen, korrigieren, dann als ausfüllbares PDF speichern."],
    "Ui_FieldTotal": ["{0} fields: {1}", "記入欄 {0} 個: {1}", "{0} 个栏位：{1}", "{0} 個欄位：{1}", "입력란 {0}개: {1}", "{0} champs : {1}", "{0} Felder: {1}"],
    "Ui_SectionFind": ["FIND FIELDS", "記入欄を探す", "查找栏位", "尋找欄位", "입력란 찾기", "TROUVER LES CHAMPS", "FELDER FINDEN"],
    "Ui_SectionEdit": ["EDIT", "編集", "编辑", "編輯", "편집", "MODIFIER", "BEARBEITEN"],
    "Ui_SectionOutput": ["OUTPUT", "出力", "输出", "輸出", "출력", "SORTIE", "AUSGABE"],
    "Ui_EditKeys": ["V select · F draw a field · P preview · Tab next field · Enter rename · Delete remove · arrows nudge (Shift: 5 pt)",
                    "V 選択 · F 記入欄を描く · P プレビュー · Tab 次の記入欄 · Enter 名前を変更 · Delete 削除 · 矢印 微調整（Shift: 5 pt）",
                    "V 选择 · F 画栏位 · P 预览 · Tab 下一个栏位 · Enter 改名 · Delete 删除 · 方向键 微调（Shift：5 pt）",
                    "V 選取 · F 繪製欄位 · P 預覽 · Tab 下一個欄位 · Enter 改名 · Delete 刪除 · 方向鍵 微調（Shift：5 pt）",
                    "V 선택 · F 입력란 그리기 · P 미리 보기 · Tab 다음 입력란 · Enter 이름 변경 · Delete 삭제 · 화살표 미세 조정 (Shift: 5 pt)",
                    "V sélection · F dessiner un champ · P aperçu · Tab champ suivant · Entrée renommer · Suppr retirer · flèches ajuster (Maj : 5 pt)",
                    "V Auswahl · F Feld zeichnen · P Vorschau · Tab nächstes Feld · Enter umbenennen · Entf löschen · Pfeile verschieben (Umschalt: 5 pt)"],
    "Ui_EmbedFontCaption": ["Embedded font", "埋め込むフォント", "嵌入字体", "內嵌字型", "포함할 글꼴", "Police incorporée", "Eingebettete Schrift"],
    "Ui_EmbedFontNote": ["Optional. Embeds a whole font so rare characters (e.g. 燁) show in every viewer.",
                         "任意。フォント全体を埋め込み、難しい漢字（例: 燁）をどのビューアでも表示します。",
                         "可选。嵌入整套字体，让生僻字（如 燁）在任何阅读器中都能显示。",
                         "選用。內嵌整套字型，讓罕用字（如 燁）在任何閱讀器都能顯示。",
                         "선택 사항. 글꼴 전체를 포함해 드문 한자(예: 燁)도 모든 뷰어에서 보이게 합니다.",
                         "Facultatif. Incorpore une police entière pour que les caractères rares (ex. 燁) s’affichent partout.",
                         "Optional. Bettet eine ganze Schrift ein, damit seltene Zeichen (z. B. 燁) überall angezeigt werden."],
    "Ui_LoadShort": ["Load…", "読み込む…", "载入…", "載入…", "불러오기…", "Charger…", "Laden…"],
    "Ui_SaveShort": ["Save…", "保存…", "保存…", "儲存…", "저장…", "Enregistrer…", "Speichern…"],
    "Ui_Remove": ["Remove", "外す", "移除", "移除", "제거", "Retirer", "Entfernen"],
    "Ui_SelectTip": ["Select and move fields (V)", "選択・移動（V）", "选择和移动栏位（V）", "選取和移動欄位（V）", "입력란 선택·이동 (V)", "Sélectionner et déplacer (V)", "Felder auswählen und verschieben (V)"],
    "Ui_DrawTip": ["Draw a new field (F)", "新しい記入欄を描く（F）", "画新栏位（F）", "繪製新欄位（F）", "새 입력란 그리기 (F)", "Dessiner un nouveau champ (F)", "Neues Feld zeichnen (F)"],
    "Ui_DeleteTip": ["Delete the selected field (Delete)", "選択した記入欄を削除（Delete）", "删除选中的栏位（Delete）", "刪除選取的欄位（Delete）", "선택한 입력란 삭제 (Delete)", "Supprimer le champ sélectionné (Suppr)", "Ausgewähltes Feld löschen (Entf)"],
    "Ui_FieldsPanel": ["Fields", "記入欄", "栏位", "欄位", "입력란", "Champs", "Felder"],
    "Ui_PagesPanel": ["Pages", "ページ", "页面", "頁面", "페이지", "Pages", "Seiten"],
    "Ui_OcrTitle": ["Make a scan searchable", "スキャンを検索可能に", "让扫描件可搜索", "讓掃描檔可搜尋", "스캔을 검색 가능하게", "Rendre un scan cherchable", "Scan durchsuchbar machen"],
    "Ui_OcrLead": ["Recognises the text of scanned pages and adds it as an invisible layer. The pages look the same.",
                   "スキャンしたページの文字を認識し、見えない文字層として加えます。見た目は変わりません。",
                   "识别扫描页上的文字，并作为看不见的文字层加入。页面外观不变。",
                   "辨識掃描頁上的文字，並作為看不見的文字層加入。頁面外觀不變。",
                   "스캔한 페이지의 글자를 인식해 보이지 않는 텍스트 레이어로 추가합니다. 겉모습은 그대로입니다.",
                   "Reconnaît le texte des pages numérisées et l’ajoute en couche invisible. Les pages ne changent pas.",
                   "Erkennt den Text gescannter Seiten und fügt ihn als unsichtbare Ebene hinzu. Die Seiten bleiben gleich."],
    "Ui_OcrPlan": ["{0} of {1} page(s) need OCR.", "{1} ページ中 {0} ページに OCR が必要です。", "{1} 页中有 {0} 页需要 OCR。", "{1} 頁中有 {0} 頁需要 OCR。", "{1}페이지 중 {0}페이지에 OCR 이 필요합니다.", "{0} page(s) sur {1} à reconnaître.", "{0} von {1} Seite(n) brauchen OCR."],
    "Ui_OcrNothing": ["Every page already has text, so nothing needs OCR. Tick the box below to recognise them anyway.",
                      "どのページにも文字情報があるため、OCR は不要です。それでも認識するには下のチェックボックスをオンにします。",
                      "每页都已有文字，不需要 OCR。如仍要识别，请勾选下方的选项。",
                      "每頁都已有文字，不需要 OCR。如仍要辨識，請勾選下方的選項。",
                      "모든 페이지에 이미 텍스트가 있어 OCR 이 필요 없습니다. 그래도 인식하려면 아래 상자를 체크하세요.",
                      "Chaque page a déjà du texte : aucun OCR nécessaire. Cochez la case ci-dessous pour reconnaître quand même.",
                      "Jede Seite hat bereits Text, OCR ist nicht nötig. Das Kästchen unten anhaken, um trotzdem zu erkennen."],
    "Ui_OcrEngine": ["ENGINE", "エンジン", "引擎", "引擎", "엔진", "MOTEUR", "ENGINE"],
    "Ui_EngineAuto": ["Automatic (on this computer)", "自動（このコンピュータ上）", "自动（在本机）", "自動（在本機）", "자동 (이 컴퓨터에서)", "Automatique (sur cet ordinateur)", "Automatisch (auf diesem Computer)"],
    "Ui_EngineAi": ["AI model (sends page images)", "AI モデル（ページ画像を送信）", "AI 模型（会发送页面图像）", "AI 模型（會傳送頁面影像）", "AI 모델 (페이지 이미지 전송)", "Modèle d’IA (envoie les images)", "KI-Modell (sendet Seitenbilder)"],
    "Ui_OcrEngineNote": ["Apple Vision and Tesseract run on this computer. The AI model reads handwriting best, but asks before it sends pages.",
                         "Apple Vision と Tesseract はこのコンピュータ上で動きます。AI モデルは手書きに強いですが、送信前に確認します。",
                         "Apple Vision 和 Tesseract 在本机运行。AI 模型最擅长手写，但发送页面前会先询问。",
                         "Apple Vision 和 Tesseract 在本機執行。AI 模型最擅長手寫，但傳送頁面前會先詢問。",
                         "Apple Vision 과 Tesseract 는 이 컴퓨터에서 실행됩니다. AI 모델은 손글씨에 강하지만 보내기 전에 확인합니다.",
                         "Apple Vision et Tesseract tournent sur cet ordinateur. Le modèle d’IA lit mieux l’écriture manuscrite, mais demande avant d’envoyer.",
                         "Apple Vision und Tesseract laufen auf diesem Computer. Das KI-Modell liest Handschrift am besten, fragt aber vor dem Senden."],
    "Ui_OcrLanguages": ["LANGUAGES", "言語", "语言", "語言", "언어", "LANGUES", "SPRACHEN"],
    "Ui_OcrForce": ["Also pages that already have text", "文字のあるページも認識する", "也识别已有文字的页面", "也辨識已有文字的頁面", "글자가 있는 페이지도 인식", "Aussi les pages qui ont déjà du texte", "Auch Seiten, die schon Text haben"],
    "Ui_OcrRun": ["Make searchable PDF…", "検索可能な PDF を作成…", "生成可搜索的 PDF…", "產生可搜尋的 PDF…", "검색 가능한 PDF 만들기…", "Créer le PDF cherchable…", "Durchsuchbares PDF erstellen…"],
    "Ui_OcrProgress": ["Recognising page {0} of {1}…", "{1} ページ中 {0} ページ目を認識中…", "正在识别第 {0}/{1} 页…", "正在辨識第 {0}/{1} 頁…", "{1}페이지 중 {0}페이지 인식 중…", "Reconnaissance de la page {0} sur {1}…", "Seite {0} von {1} wird erkannt…"],
    "Ui_CombineTitle": ["Combine files", "ファイルを結合", "合并文件", "合併檔案", "파일 합치기", "Assembler des fichiers", "Dateien zusammenfügen"],
    "Ui_CombineLead": ["Puts PDFs, photos and scans together in the order below. Form fields stay fillable; each image becomes a page.", "下の順番で PDF・写真・スキャン画像をまとめます。記入欄はそのまま記入でき、画像は 1 枚ずつページになります。", "按下面的顺序合并 PDF、照片和扫描图片。表单栏位仍可填写，每张图片成为一页。", "依下方順序合併 PDF、照片和掃描圖片。表單欄位仍可填寫，每張圖片成為一頁。", "아래 순서대로 PDF, 사진, 스캔 이미지를 합칩니다. 입력란은 계속 입력할 수 있고, 이미지는 한 장씩 페이지가 됩니다.", "Assemble les PDF, photos et numérisations dans l’ordre ci-dessous. Les champs restent remplissables ; chaque image devient une page.", "Fügt PDFs, Fotos und Scans in der Reihenfolge unten zusammen. Formularfelder bleiben ausfüllbar; jedes Bild wird eine Seite."],
    "Ui_AddFiles": ["Add files…", "ファイルを追加…", "添加文件…", "加入檔案…", "파일 추가…", "Ajouter…", "Dateien hinzufügen…"],
    "Ui_MoveUp": ["Move up", "上へ", "上移", "上移", "위로", "Monter", "Nach oben"],
    "Ui_MoveDown": ["Move down", "下へ", "下移", "下移", "아래로", "Descendre", "Nach unten"],
    "Ui_CombineEmpty": ["Add PDFs or images (JPEG, PNG…), or drop them here.", "PDF や画像（JPEG・PNG など）を追加するか、ここにドロップしてください。", "添加 PDF 或图片（JPEG、PNG 等），或拖到这里。", "加入 PDF 或圖片（JPEG、PNG 等），或拖到這裡。", "PDF 나 이미지(JPEG, PNG 등)를 추가하거나 여기로 끌어다 놓으세요.", "Ajoutez des PDF ou des images (JPEG, PNG…), ou déposez-les ici.", "PDFs oder Bilder (JPEG, PNG …) hinzufügen oder hier ablegen."],
    "Ui_ProtectedFile": ["protected: cannot be combined", "保護あり: 結合できません", "受保护：无法合并", "受保護：無法合併", "보호됨: 합칠 수 없음", "protégé : impossible à assembler", "geschützt: nicht kombinierbar"],
    "Ui_ProtectedNote": ["{0} is protected: its permissions forbid reusing its pages. Washi does not remove protection.",
                         "{0} は保護されています。ページの再利用が許可されていません。Washi は保護を解除しません。",
                         "{0} 受保护：其权限禁止重用页面。Washi 不会解除保护。",
                         "{0} 受保護：其權限禁止重複使用頁面。Washi 不會解除保護。",
                         "{0} 은(는) 보호되어 있어 페이지를 재사용할 수 없습니다. Washi 는 보호를 해제하지 않습니다.",
                         "{0} est protégé : ses droits interdisent de réutiliser ses pages. Washi ne retire pas la protection.",
                         "{0} ist geschützt: Die Rechte verbieten, seine Seiten weiterzuverwenden. Washi hebt keinen Schutz auf."],
    "Ui_CombineRun": ["Combine…", "結合…", "合并…", "合併…", "합치기…", "Assembler…", "Zusammenfügen…"],
    "Ui_PageCount": ["{0} page(s)", "{0} ページ", "{0} 页", "{0} 頁", "{0}페이지", "{0} page(s)", "{0} Seite(n)"],
    "Ui_SplitTitle": ["Split into files", "ファイルに分割", "拆分为多个文件", "分割為多個檔案", "여러 파일로 나누기", "Diviser en fichiers", "In Dateien teilen"],
    "Ui_SplitLead": ["Writes one PDF per page range into a folder. Form fields stay fillable.",
                     "ページ範囲ごとに 1 つの PDF をフォルダに書き出します。記入欄はそのまま記入できます。",
                     "每个页码范围写成一个 PDF，存到文件夹中。表单栏位仍可填写。",
                     "每個頁碼範圍寫成一個 PDF，存到資料夾中。表單欄位仍可填寫。",
                     "페이지 범위마다 PDF 하나씩 폴더에 저장합니다. 입력란은 계속 입력할 수 있습니다.",
                     "Écrit un PDF par plage de pages dans un dossier. Les champs restent remplissables.",
                     "Schreibt pro Seitenbereich ein PDF in einen Ordner. Formularfelder bleiben ausfüllbar."],
    "Ui_Ranges": ["PAGE RANGES", "ページ範囲", "页码范围", "頁碼範圍", "페이지 범위", "PLAGES DE PAGES", "SEITENBEREICHE"],
    "Ui_RangesHint": ["For example 1-3, 5. Leave empty for one file per page.", "例: 1-3, 5。空欄なら 1 ページずつ分けます。", "例如 1-3, 5。留空则每页一个文件。", "例如 1-3, 5。留空則每頁一個檔案。", "예: 1-3, 5. 비워 두면 한 페이지씩 나눕니다.", "Par exemple 1-3, 5. Laissez vide pour un fichier par page.", "Zum Beispiel 1-3, 5. Leer lassen für eine Datei pro Seite."],
    "Ui_SplitPreview": ["{0} file(s): {1}", "{0} 個のファイル: {1}", "{0} 个文件：{1}", "{0} 個檔案：{1}", "파일 {0}개: {1}", "{0} fichier(s) : {1}", "{0} Datei(en): {1}"],
    "Ui_SplitRun": ["Split into folder…", "フォルダに分割…", "拆分到文件夹…", "分割到資料夾…", "폴더로 나누기…", "Diviser dans un dossier…", "In Ordner teilen…"],
    "Ui_SplitDone": ["{0} file(s) written to {1}", "{0} 個のファイルを {1} に書き出しました", "已将 {0} 个文件写入 {1}", "已將 {0} 個檔案寫入 {1}", "파일 {0}개를 {1}에 저장했습니다", "{0} fichier(s) écrit(s) dans {1}", "{0} Datei(en) in {1} geschrieben"],
    "Ui_ToolOrganize": ["Organize", "ページ整理", "整理页面", "整理頁面", "페이지 정리", "Organiser", "Ordnen"],
    "Ui_OrganizeTitle": ["Organize pages", "ページを整理", "整理页面", "整理頁面", "페이지 정리", "Organiser les pages", "Seiten ordnen"],
    "Ui_OrganizeLead": ["Reorder, turn, remove or insert pages, then save a new PDF. Form fields stay fillable.",
                        "ページの並べ替え・回転・削除・挿入をして、新しい PDF として保存します。記入欄はそのまま使えます。",
                        "调整顺序、旋转、删除或插入页面，然后另存为新的 PDF。表单栏位仍可填写。",
                        "調整順序、旋轉、刪除或插入頁面，然後另存為新的 PDF。表單欄位仍可填寫。",
                        "페이지 순서를 바꾸고, 돌리고, 지우거나 끼워 넣은 뒤 새 PDF 로 저장합니다. 입력란은 그대로 쓸 수 있습니다.",
                        "Réordonnez, faites pivoter, retirez ou insérez des pages, puis enregistrez un nouveau PDF. Les champs restent remplissables.",
                        "Seiten umordnen, drehen, entfernen oder einfügen und als neues PDF speichern. Formularfelder bleiben ausfüllbar."],
    "Ui_OrganizeInsert": ["Insert pages…", "ページを挿入…", "插入页面…", "插入頁面…", "페이지 삽입…", "Insérer des pages…", "Seiten einfügen…"],
    "Ui_OrganizeInsertTip": ["Adds every page of other PDFs after the selected page", "選択したページの後ろに、ほかの PDF の全ページを追加します", "把其他 PDF 的所有页面插到选中页面之后", "把其他 PDF 的所有頁面插到選取頁面之後", "선택한 페이지 뒤에 다른 PDF 의 모든 페이지를 넣습니다", "Ajoute toutes les pages d’autres PDF après la page sélectionnée", "Fügt alle Seiten anderer PDFs nach der ausgewählten Seite ein"],
    "Ui_RotateLeft": ["Turn left", "左に回転", "向左旋转", "向左旋轉", "왼쪽으로 회전", "Pivoter à gauche", "Nach links drehen"],
    "Ui_RotateRight": ["Turn right", "右に回転", "向右旋转", "向右旋轉", "오른쪽으로 회전", "Pivoter à droite", "Nach rechts drehen"],
    "Ui_OrganizeReset": ["Start over", "最初に戻す", "全部重来", "全部重來", "처음부터", "Recommencer", "Neu beginnen"],
    "Ui_OrganizeSummary": ["{0} page(s) · {1} removed · {2} turned · {3} inserted", "{0} ページ · 削除 {1} · 回転 {2} · 挿入 {3}", "{0} 页 · 删除 {1} · 旋转 {2} · 插入 {3}", "{0} 頁 · 刪除 {1} · 旋轉 {2} · 插入 {3}", "{0}페이지 · 삭제 {1} · 회전 {2} · 삽입 {3}", "{0} page(s) · {1} retirée(s) · {2} pivotée(s) · {3} insérée(s)", "{0} Seite(n) · {1} entfernt · {2} gedreht · {3} eingefügt"],
    "Ui_OrganizeFrom": ["p{0} · {1}", "{1} の {0} ページ", "{1} 第 {0} 页", "{1} 第 {0} 頁", "{1} {0}페이지", "p. {0} · {1}", "S. {0} · {1}"],
    "Ui_OrganizeEmpty": ["Open a PDF to organize its pages.", "ページを整理する PDF を開いてください。", "请打开要整理页面的 PDF。", "請開啟要整理頁面的 PDF。", "페이지를 정리할 PDF 를 여세요.", "Ouvrez un PDF pour organiser ses pages.", "Öffnen Sie ein PDF, um seine Seiten zu ordnen."],
    "Ui_OrganizeRun": ["Save as new PDF…", "新しい PDF として保存…", "另存为新 PDF…", "另存為新 PDF…", "새 PDF 로 저장…", "Enregistrer un nouveau PDF…", "Als neues PDF speichern…"],
    # ---- shared buttons
    "Ui_Save": ["Save", "保存", "保存", "儲存", "저장", "Enregistrer", "Speichern"],
    "Ui_Copy": ["Copy", "コピー", "复制", "複製", "복사", "Copier", "Kopieren"],
    "Ui_Copied": ["Copied.", "コピーしました。", "已复制。", "已複製。", "복사했습니다.", "Copié.", "Kopiert."],
    "Ui_Show": ["Show", "表示", "显示", "顯示", "보기", "Afficher", "Anzeigen"],
    "Ui_Yes": ["yes", "はい", "是", "是", "예", "oui", "ja"],
    "Ui_No": ["no", "いいえ", "否", "否", "아니요", "non", "nein"],
    # ---- Settings: AI model
    "Ui_AiTab": ["AI model", "AI モデル", "AI 模型", "AI 模型", "AI 모델", "Modèle d’IA", "KI-Modell"],
    "Ui_AiProvider": ["Provider", "プロバイダ", "提供方", "提供者", "제공자", "Fournisseur", "Anbieter"],
    "Ui_AiProviderLocal": ["Local / OpenAI-compatible", "ローカル / OpenAI 互換", "本地 / OpenAI 兼容", "本機 / OpenAI 相容", "로컬 / OpenAI 호환", "Local / compatible OpenAI", "Lokal / OpenAI-kompatibel"],
    "Ui_AiModel": ["Model", "モデル", "模型", "模型", "모델", "Modèle", "Modell"],
    "Ui_AiEndpoint": ["Endpoint", "エンドポイント", "接口地址", "端點", "엔드포인트", "Point d’accès", "Endpunkt"],
    "Ui_AiEndpointNote": ["Leave empty for the provider's own API. A local server (Ollama, LM Studio) keeps everything on this Mac.",
                          "空欄ならプロバイダ標準の API を使います。ローカルサーバー（Ollama、LM Studio）なら何もこの Mac から出ません。",
                          "留空则使用提供方自己的 API。本地服务器（Ollama、LM Studio）不会把任何内容发出这台 Mac。",
                          "留空則使用提供者自己的 API。本機伺服器（Ollama、LM Studio）不會把任何內容送出這台 Mac。",
                          "비워 두면 제공자의 기본 API 를 씁니다. 로컬 서버(Ollama, LM Studio)를 쓰면 아무것도 이 Mac 밖으로 나가지 않습니다.",
                          "Laissez vide pour l’API du fournisseur. Un serveur local (Ollama, LM Studio) garde tout sur ce Mac.",
                          "Leer lassen für die API des Anbieters. Ein lokaler Server (Ollama, LM Studio) behält alles auf diesem Mac."],
    "Ui_AiBadEndpoint": ["The endpoint is not a valid URL.", "エンドポイントが正しい URL ではありません。", "接口地址不是有效的 URL。", "端點不是有效的 URL。", "엔드포인트가 올바른 URL 이 아닙니다.", "Le point d’accès n’est pas une URL valide.", "Der Endpunkt ist keine gültige URL."],
    "Ui_AiKey": ["API key", "API キー", "API key", "API key", "API 키", "Clé d’API", "API-Schlüssel"],
    "Ui_AiKeyWatermark": ["Paste a new key to replace the stored one", "保存済みのキーを置き換えるには新しいキーを貼り付け", "粘贴新的 key 以替换已保存的", "貼上新的 key 以取代已儲存的", "저장된 키를 바꾸려면 새 키를 붙여 넣으세요", "Collez une nouvelle clé pour remplacer l’actuelle", "Neuen Schlüssel einfügen, um den gespeicherten zu ersetzen"],
    "Ui_AiKeyRemove": ["Remove key", "キーを削除", "删除 key", "刪除 key", "키 삭제", "Supprimer la clé", "Schlüssel entfernen"],
    "Ui_AiKeyFromEnv": ["Key from the environment variable {0} (it wins over the Keychain).", "環境変数 {0} のキーを使用中（キーチェーンより優先）。", "使用环境变量 {0} 中的 key（优先于钥匙串）。", "使用環境變數 {0} 中的 key（優先於鑰匙圈）。", "환경 변수 {0} 의 키를 사용 중입니다(키체인보다 우선).", "Clé de la variable d’environnement {0} (prioritaire sur le trousseau).", "Schlüssel aus der Umgebungsvariable {0} (hat Vorrang vor dem Schlüsselbund)."],
    "Ui_AiKeyStored": ["Key in the Keychain: {0}", "キーチェーンのキー: {0}", "钥匙串中的 key：{0}", "鑰匙圈中的 key：{0}", "키체인의 키: {0}", "Clé dans le trousseau : {0}", "Schlüssel im Schlüsselbund: {0}"],
    "Ui_AiKeyOptional": ["No key stored (a local server usually needs none).", "キーは未保存です（ローカルサーバーには通常不要）。", "未保存 key（本地服务器通常不需要）。", "未儲存 key（本機伺服器通常不需要）。", "저장된 키가 없습니다(로컬 서버는 보통 필요 없음).", "Aucune clé (un serveur local n’en a généralement pas besoin).", "Kein Schlüssel gespeichert (ein lokaler Server braucht meist keinen)."],
    "Ui_AiKeyMissing": ["No key yet: paste one above and save.", "キーがありません。上に貼り付けて保存してください。", "还没有 key：请在上方粘贴并保存。", "還沒有 key：請在上方貼上並儲存。", "키가 없습니다. 위에 붙여 넣고 저장하세요.", "Pas encore de clé : collez-la ci-dessus et enregistrez.", "Noch kein Schlüssel: oben einfügen und speichern."],
    "Ui_AiVision": ["Send page images (turn off for text-only models)", "ページ画像を送る（テキスト専用モデルではオフ）", "发送页面图像（纯文本模型请关闭）", "傳送頁面影像（純文字模型請關閉）", "페이지 이미지 보내기(텍스트 전용 모델은 끄기)", "Envoyer les images des pages (à désactiver pour les modèles texte seul)", "Seitenbilder senden (bei reinen Textmodellen ausschalten)"],
    "Ui_AiTest": ["Test connection", "接続テスト", "测试连接", "測試連線", "연결 테스트", "Tester la connexion", "Verbindung testen"],
    "Ui_AiTesting": ["Testing…", "テスト中…", "正在测试…", "正在測試…", "테스트 중…", "Test en cours…", "Teste…"],
    "Ui_AiSaved": ["Saved.", "保存しました。", "已保存。", "已儲存。", "저장했습니다.", "Enregistré.", "Gespeichert."],
    "Ui_AiTestOk": ["{0} answered. {1} models available; tool calls: {2}.", "{0} が応答しました。利用可能なモデル {1} 個、ツール呼び出し: {2}。", "{0} 已响应。可用模型 {1} 个；工具调用：{2}。", "{0} 已回應。可用模型 {1} 個；工具呼叫：{2}。", "{0} 이(가) 응답했습니다. 사용 가능한 모델 {1}개, 도구 호출: {2}.", "{0} a répondu. {1} modèles disponibles ; appels d’outils : {2}.", "{0} hat geantwortet. {1} Modelle verfügbar; Werkzeugaufrufe: {2}."],
    # ---- Settings: AI & MCP access
    "Ui_McpTab": ["AI & MCP", "AI と MCP", "AI 与 MCP", "AI 與 MCP", "AI 및 MCP", "IA et MCP", "KI & MCP"],
    "Ui_AccessCaption": ["FOLDERS AI MAY USE", "AI が使えるフォルダ", "AI 可使用的文件夹", "AI 可使用的資料夾", "AI 가 쓸 수 있는 폴더", "DOSSIERS ACCESSIBLES À L’IA", "ORDNER FÜR DIE KI"],
    "Ui_AccessNote": ["The assistant and MCP clients (Claude Code, Claude Desktop, …) only reach PDFs inside these folders. They create new files and never overwrite.",
                      "アシスタントと MCP クライアント（Claude Code、Claude Desktop など）は、これらのフォルダ内の PDF にだけアクセスします。新しいファイルを作るだけで、上書きはしません。",
                      "助手和 MCP 客户端（Claude Code、Claude Desktop 等）只能访问这些文件夹中的 PDF。它们只创建新文件，从不覆盖。",
                      "助理和 MCP 用戶端（Claude Code、Claude Desktop 等）只能存取這些資料夾中的 PDF。它們只建立新檔案，從不覆寫。",
                      "어시스턴트와 MCP 클라이언트(Claude Code, Claude Desktop 등)는 이 폴더 안의 PDF 에만 접근합니다. 새 파일만 만들고 덮어쓰지 않습니다.",
                      "L’assistant et les clients MCP (Claude Code, Claude Desktop…) n’accèdent qu’aux PDF de ces dossiers. Ils créent de nouveaux fichiers et n’écrasent jamais.",
                      "Der Assistent und MCP-Clients (Claude Code, Claude Desktop, …) erreichen nur PDFs in diesen Ordnern. Sie legen neue Dateien an und überschreiben nie."],
    "Ui_AddFolder": ["Add folder…", "フォルダを追加…", "添加文件夹…", "新增資料夾…", "폴더 추가…", "Ajouter un dossier…", "Ordner hinzufügen…"],
    "Ui_AccessOpenDocument": ["Also the folder of the PDF open in Washi", "Washi で開いている PDF のフォルダも含める", "同时包括 Washi 中打开的 PDF 所在文件夹", "同時包括 Washi 中開啟的 PDF 所在資料夾", "Washi 에서 연 PDF 의 폴더도 포함", "Ainsi que le dossier du PDF ouvert dans Washi", "Auch den Ordner des in Washi geöffneten PDFs"],
    "Ui_WritesCaption": ["Writing files", "ファイルの書き込み", "写入文件", "寫入檔案", "파일 쓰기", "Écriture de fichiers", "Dateien schreiben"],
    "Ui_WritesAsk": ["Ask me each time (recommended)", "毎回確認する（推奨）", "每次询问我（推荐）", "每次詢問我（建議）", "매번 묻기(권장)", "Me demander à chaque fois (recommandé)", "Jedes Mal fragen (empfohlen)"],
    "Ui_WritesAllow": ["Allow without asking", "確認せずに許可", "不询问直接允许", "不詢問直接允許", "묻지 않고 허용", "Autoriser sans demander", "Ohne Nachfrage erlauben"],
    "Ui_WritesDeny": ["Read only: never write", "読み取りのみ（書き込まない）", "只读：从不写入", "唯讀：從不寫入", "읽기 전용: 쓰지 않음", "Lecture seule : jamais d’écriture", "Nur lesen: nie schreiben"],
    "Ui_WritesNote": ["Approvals appear in the assistant chat, or as a card over the page for MCP clients. An unanswered request is declined after 2 minutes.",
                      "確認はアシスタントのチャット内に、MCP クライアントの場合はページ上のカードとして表示されます。2 分間応答がなければ拒否されます。",
                      "确认请求会出现在助手对话中；MCP 客户端的请求以卡片形式显示在页面上方。2 分钟内未回应则拒绝。",
                      "確認請求會出現在助理對話中；MCP 用戶端的請求以卡片形式顯示在頁面上方。2 分鐘內未回應則拒絕。",
                      "승인 요청은 어시스턴트 채팅 안에, MCP 클라이언트는 페이지 위 카드로 표시됩니다. 2분 동안 응답이 없으면 거부됩니다.",
                      "Les demandes s’affichent dans la discussion de l’assistant, ou en carte au-dessus de la page pour les clients MCP. Sans réponse, elles sont refusées après 2 minutes.",
                      "Freigaben erscheinen im Assistenten-Chat bzw. für MCP-Clients als Karte über der Seite. Unbeantwortete Anfragen werden nach 2 Minuten abgelehnt."],
    "Ui_McpCaption": ["MCP SERVER", "MCP サーバー", "MCP 服务器", "MCP 伺服器", "MCP 서버", "SERVEUR MCP", "MCP-SERVER"],
    "Ui_McpOn": ["On", "オン", "开", "開", "켬", "Activé", "An"],
    "Ui_McpOff": ["Off", "オフ", "关", "關", "끔", "Désactivé", "Aus"],
    "Ui_McpNote": ["Lets AI agents use Washi's PDF tools, the folders above and the document open here, while Washi runs. HTTP listens on 127.0.0.1 only and needs the token.",
                   "Washi の起動中、AI エージェントが Washi の PDF ツール、上のフォルダ、ここで開いている文書を使えるようにします。HTTP は 127.0.0.1 のみで待ち受け、トークンが必要です。",
                   "Washi 运行时，让 AI 代理使用 Washi 的 PDF 工具、上面的文件夹和这里打开的文档。HTTP 只监听 127.0.0.1，并且需要令牌。",
                   "Washi 執行時，讓 AI 代理使用 Washi 的 PDF 工具、上面的資料夾和這裡開啟的文件。HTTP 只監聽 127.0.0.1，並且需要權杖。",
                   "Washi 가 실행 중일 때 AI 에이전트가 Washi 의 PDF 도구, 위 폴더, 여기서 연 문서를 쓸 수 있게 합니다. HTTP 는 127.0.0.1 에서만 열리며 토큰이 필요합니다.",
                   "Permet aux agents d’IA d’utiliser les outils PDF de Washi, les dossiers ci-dessus et le document ouvert ici, tant que Washi tourne. HTTP n’écoute que sur 127.0.0.1 et exige le jeton.",
                   "Erlaubt KI-Agenten, Washis PDF-Werkzeuge, die Ordner oben und das hier geöffnete Dokument zu nutzen, solange Washi läuft. HTTP lauscht nur auf 127.0.0.1 und braucht das Token."],
    "Ui_McpClaudeCode": ["Claude Code (run in a terminal)", "Claude Code（ターミナルで実行）", "Claude Code（在终端中运行）", "Claude Code（在終端機中執行）", "Claude Code (터미널에서 실행)", "Claude Code (à lancer dans un terminal)", "Claude Code (im Terminal ausführen)"],
    "Ui_McpClaudeDesktop": ["Claude Desktop (claude_desktop_config.json)", "Claude Desktop（claude_desktop_config.json）", "Claude Desktop（claude_desktop_config.json）", "Claude Desktop（claude_desktop_config.json）", "Claude Desktop (claude_desktop_config.json)", "Claude Desktop (claude_desktop_config.json)", "Claude Desktop (claude_desktop_config.json)"],
    "Ui_McpOtherClients": ["Other clients (Streamable HTTP)", "その他のクライアント（Streamable HTTP）", "其他客户端（Streamable HTTP）", "其他用戶端（Streamable HTTP）", "기타 클라이언트 (Streamable HTTP)", "Autres clients (Streamable HTTP)", "Andere Clients (Streamable HTTP)"],
    "Ui_McpToken": ["Token", "トークン", "令牌", "權杖", "토큰", "Jeton", "Token"],
    "Ui_McpRegenerate": ["New token", "トークンを再発行", "重新生成令牌", "重新產生權杖", "새 토큰", "Nouveau jeton", "Neues Token"],
    "Ui_McpTokenNote": ["A new token locks out every HTTP client until you set it up again. washi-mcp (Claude Desktop) needs no token.",
                        "新しいトークンにすると、設定し直すまですべての HTTP クライアントが接続できなくなります。washi-mcp（Claude Desktop）にはトークン不要です。",
                        "生成新令牌后，所有 HTTP 客户端都要重新配置才能连接。washi-mcp（Claude Desktop）不需要令牌。",
                        "產生新權杖後，所有 HTTP 用戶端都要重新設定才能連線。washi-mcp（Claude Desktop）不需要權杖。",
                        "새 토큰을 만들면 다시 설정할 때까지 모든 HTTP 클라이언트가 차단됩니다. washi-mcp(Claude Desktop)에는 토큰이 필요 없습니다.",
                        "Un nouveau jeton bloque tous les clients HTTP jusqu’à leur reconfiguration. washi-mcp (Claude Desktop) n’en a pas besoin.",
                        "Ein neues Token sperrt alle HTTP-Clients aus, bis sie neu eingerichtet sind. washi-mcp (Claude Desktop) braucht kein Token."],
    "Ui_McpClients": ["Recent clients", "最近のクライアント", "最近的客户端", "最近的用戶端", "최근 클라이언트", "Clients récents", "Letzte Clients"],
    "Ui_McpNoClients": ["No client has connected yet.", "まだクライアントは接続していません。", "还没有客户端连接。", "還沒有用戶端連線。", "아직 연결한 클라이언트가 없습니다.", "Aucun client ne s’est encore connecté.", "Noch kein Client verbunden."],
    "Ui_McpDisconnect": ["Disconnect", "切断", "断开", "中斷連線", "연결 끊기", "Déconnecter", "Trennen"],
    "Ui_McpClientDetail": ["{0} · {1} calls · last {2}", "{0} · 呼び出し {1} 回 · 最終 {2}", "{0} · 调用 {1} 次 · 最近 {2}", "{0} · 呼叫 {1} 次 · 最近 {2}", "{0} · 호출 {1}회 · 마지막 {2}", "{0} · {1} appels · dernier {2}", "{0} · {1} Aufrufe · zuletzt {2}"],
    "Mcp_Off": ["The MCP server is off.", "MCP サーバーはオフです。", "MCP 服务器已关闭。", "MCP 伺服器已關閉。", "MCP 서버가 꺼져 있습니다.", "Le serveur MCP est désactivé.", "Der MCP-Server ist aus."],
    "Mcp_Starting": ["Starting…", "起動中…", "正在启动…", "正在啟動…", "시작하는 중…", "Démarrage…", "Startet…"],
    "Mcp_Running": ["Running. HTTP: {0} · local socket: {1}", "動作中。HTTP: {0} · ローカルソケット: {1}", "运行中。HTTP：{0} · 本地套接字：{1}", "執行中。HTTP：{0} · 本機通訊端：{1}", "실행 중. HTTP: {0} · 로컬 소켓: {1}", "En marche. HTTP : {0} · socket local : {1}", "Läuft. HTTP: {0} · lokaler Socket: {1}"],
    "Mcp_ErrInstance": ["Another Washi already serves MCP ({0}). Quit it, or turn its MCP server off.", "別の Washi がすでに MCP を提供しています（{0}）。終了するか、その MCP サーバーをオフにしてください。", "另一个 Washi 已在提供 MCP（{0}）。请退出它，或关闭它的 MCP 服务器。", "另一個 Washi 已在提供 MCP（{0}）。請結束它，或關閉它的 MCP 伺服器。", "다른 Washi 가 이미 MCP 를 제공 중입니다({0}). 종료하거나 그쪽 MCP 서버를 끄세요.", "Un autre Washi fournit déjà MCP ({0}). Quittez-le ou désactivez son serveur MCP.", "Ein anderes Washi stellt MCP schon bereit ({0}). Beenden oder dessen MCP-Server ausschalten."],
    "Mcp_ErrStart": ["The MCP server could not start: {0}", "MCP サーバーを起動できませんでした: {0}", "MCP 服务器无法启动：{0}", "MCP 伺服器無法啟動：{0}", "MCP 서버를 시작할 수 없습니다: {0}", "Le serveur MCP n’a pas pu démarrer : {0}", "Der MCP-Server konnte nicht starten: {0}"],
    # ---- approvals
    "Ui_ApprovalTitle": ["{0} wants to write a file", "{0} がファイルを書き込もうとしています", "{0} 想要写入文件", "{0} 想要寫入檔案", "{0} 이(가) 파일을 쓰려고 합니다", "{0} veut écrire un fichier", "{0} möchte eine Datei schreiben"],
    "Ui_ApprovalAllow": ["Allow", "許可", "允许", "允許", "허용", "Autoriser", "Erlauben"],
    "Ui_ApprovalDeny": ["Deny", "拒否", "拒绝", "拒絕", "거부", "Refuser", "Ablehnen"],
    "Ui_ApprovalDetails": ["Details", "詳細", "详细信息", "詳細資訊", "자세히", "Détails", "Details"],
    "Ui_ApprovalApproved": ["Allowed.", "許可しました。", "已允许。", "已允許。", "허용했습니다.", "Autorisé.", "Erlaubt."],
    "Ui_ApprovalDenied": ["Denied.", "拒否しました。", "已拒绝。", "已拒絕。", "거부했습니다.", "Refusé.", "Abgelehnt."],
    "Ui_ApprovalTimedOut": ["No answer in time: declined.", "時間内に応答がなかったため拒否しました。", "未及时回应：已拒绝。", "未及時回應：已拒絕。", "제때 응답이 없어 거부했습니다.", "Pas de réponse à temps : refusé.", "Keine rechtzeitige Antwort: abgelehnt."],
    "Ui_ApprovalCancelled": ["Cancelled.", "キャンセルされました。", "已取消。", "已取消。", "취소되었습니다.", "Annulé.", "Abgebrochen."],
    # ---- assistant
    "Ui_AssistantTitle": ["AI assistant", "AI アシスタント", "AI 助手", "AI 助理", "AI 어시스턴트", "Assistant IA", "KI-Assistent"],
    "Ui_AssistantEmpty": ["Ask about the open PDF, or have the assistant fill, merge, split or OCR PDFs in your folders. It uses the same tools as MCP clients and asks before it writes.",
                          "開いている PDF について質問したり、フォルダ内の PDF の記入・結合・分割・OCR を頼んだりできます。MCP クライアントと同じツールを使い、書き込む前に確認します。",
                          "可以询问打开的 PDF，或让助手填写、合并、拆分、OCR 文件夹中的 PDF。它与 MCP 客户端使用相同的工具，写入前会先询问。",
                          "可以詢問開啟的 PDF，或讓助理填寫、合併、分割、OCR 資料夾中的 PDF。它與 MCP 用戶端使用相同的工具，寫入前會先詢問。",
                          "열린 PDF 에 대해 묻거나 폴더 안 PDF 의 작성·병합·분할·OCR 을 맡길 수 있습니다. MCP 클라이언트와 같은 도구를 쓰며 쓰기 전에 묻습니다.",
                          "Posez des questions sur le PDF ouvert, ou demandez de remplir, fusionner, scinder ou océriser les PDF de vos dossiers. Mêmes outils que les clients MCP ; il demande avant d’écrire.",
                          "Fragen Sie zum geöffneten PDF oder lassen Sie PDFs in Ihren Ordnern ausfüllen, zusammenführen, teilen oder per OCR erkennen. Gleiche Werkzeuge wie MCP-Clients; fragt vor dem Schreiben."],
    "Ui_AssistantWatermark": ["Message (Enter to send, Shift+Enter for a new line)", "メッセージ（Enter で送信、Shift+Enter で改行）", "消息（Enter 发送，Shift+Enter 换行）", "訊息（Enter 傳送，Shift+Enter 換行）", "메시지 (Enter 보내기, Shift+Enter 줄바꿈)", "Message (Entrée pour envoyer, Maj+Entrée pour un retour à la ligne)", "Nachricht (Enter sendet, Umschalt+Enter neue Zeile)"],
    "Ui_AssistantSend": ["Send", "送信", "发送", "傳送", "보내기", "Envoyer", "Senden"],
    "Ui_AssistantStop": ["Stop", "停止", "停止", "停止", "중지", "Arrêter", "Stopp"],
    "Ui_AssistantNewChat": ["New chat", "新しいチャット", "新对话", "新對話", "새 채팅", "Nouvelle discussion", "Neuer Chat"],
    "Ui_AssistantThinking": ["Thinking", "思考", "思考过程", "思考過程", "생각", "Réflexion", "Überlegungen"],
    "Ui_AssistantDocChip": ["📄 {0} · page {1}", "📄 {0} · {1} ページ", "📄 {0} · 第 {1} 页", "📄 {0} · 第 {1} 頁", "📄 {0} · {1}쪽", "📄 {0} · page {1}", "📄 {0} · Seite {1}"],
    "Ui_AssistantDocChipTip": ["Tell the assistant which document is open (click to leave it out)", "開いている文書をアシスタントに伝えます（クリックで外す）", "告诉助手打开的是哪份文档（点击可不附带）", "告訴助理開啟的是哪份文件（點擊可不附帶）", "열린 문서를 어시스턴트에 알립니다(클릭하면 제외)", "Indique à l’assistant le document ouvert (cliquez pour l’exclure)", "Teilt dem Assistenten das geöffnete Dokument mit (Klick lässt es weg)"],
    "Ui_AssistantNotice": ["Your messages, and the document text and page images the assistant reads with its tools, are sent to {0} ({1}). Credentials in messages (keys, passwords, tokens) are masked.",
                           "メッセージと、アシスタントがツールで読んだ文書のテキストやページ画像は {0}（{1}）に送信されます。メッセージ内の認証情報（キー、パスワード、トークン）は伏せられます。",
                           "你的消息，以及助手用工具读取的文档文本和页面图像，会发送到 {0}（{1}）。消息中的凭据会被遮盖。",
                           "你的訊息，以及助理用工具讀取的文件文字和頁面影像，會傳送到 {0}（{1}）。訊息中的憑證會被遮蓋。",
                           "메시지와 어시스턴트가 도구로 읽은 문서 텍스트 및 페이지 이미지는 {0}({1})(으)로 전송됩니다. 메시지 속 인증 정보는 가려집니다.",
                           "Vos messages, ainsi que le texte et les images de pages que l’assistant lit avec ses outils, sont envoyés à {0} ({1}). Les identifiants dans les messages sont masqués.",
                           "Ihre Nachrichten sowie Dokumenttext und Seitenbilder, die der Assistent mit seinen Werkzeugen liest, gehen an {0} ({1}). Zugangsdaten in Nachrichten werden maskiert."],
    "Ui_AssistantAccept": ["Continue", "続ける", "继续", "繼續", "계속", "Continuer", "Weiter"],
    "Ui_AssistantRedacted": ["{0} secret(s) were masked before sending.", "送信前に {0} 件の秘密情報を伏せました。", "发送前遮盖了 {0} 处敏感信息。", "傳送前遮蓋了 {0} 處敏感資訊。", "보내기 전에 비밀 정보 {0}개를 가렸습니다.", "{0} secret(s) masqué(s) avant l’envoi.", "{0} Geheimnis(se) vor dem Senden maskiert."],
    "Ui_AssistantStopped": ["Stopped.", "停止しました。", "已停止。", "已停止。", "중지했습니다.", "Arrêté.", "Gestoppt."],
    "Ui_AssistantMaxTokens": ["The answer was cut off (output limit).", "回答が途中で切れました（出力上限）。", "回答被截断（输出上限）。", "回答被截斷（輸出上限）。", "답변이 잘렸습니다(출력 한도).", "La réponse a été coupée (limite de sortie).", "Die Antwort wurde abgeschnitten (Ausgabelimit)."],
    "Ui_AssistantRefused": ["The model declined this request. {0}", "モデルがこのリクエストを断りました。{0}", "模型拒绝了这个请求。{0}", "模型拒絕了這個請求。{0}", "모델이 이 요청을 거절했습니다. {0}", "Le modèle a refusé cette demande. {0}", "Das Modell hat die Anfrage abgelehnt. {0}"],
    "Ui_AssistantStepLimit": ["Stopped after too many tool steps; ask again to continue.", "ツールの手順が多すぎるため停止しました。続けるにはもう一度依頼してください。", "工具步骤过多，已停止；再问一次即可继续。", "工具步驟過多，已停止；再問一次即可繼續。", "도구 단계가 너무 많아 멈췄습니다. 계속하려면 다시 요청하세요.", "Arrêté après trop d’étapes ; redemandez pour continuer.", "Nach zu vielen Werkzeugschritten gestoppt; erneut fragen, um fortzufahren."],
    # ---- erase printed content
    "Ui_ToolErase": ["Erase", "印字を消去", "删除印字", "刪除印字", "인쇄 지우기", "Effacer", "Löschen"],
    "Ui_EraseTitle": ["Erase printed text", "印字を消去", "删除已印文字", "刪除已印文字", "인쇄된 글자 지우기", "Effacer le texte imprimé", "Gedruckten Text löschen"],
    "Ui_EraseLead": ["Drag boxes over what is already printed on the page, for example wrong data on a form. Washi deletes it from the PDF for real; nothing is just covered.",
                     "ページに印字済みの内容（様式に書かれた誤ったデータなど）の上に枠をドラッグします。Washi は PDF から本当に削除します。上から隠すだけではありません。",
                     "在页面上已印好的内容（例如表格里错误的数据）上拖出框。Washi 会从 PDF 中真正删除，而不只是盖住。",
                     "在頁面上已印好的內容（例如表單中錯誤的資料）上拖出框。Washi 會從 PDF 中真正刪除，而不只是蓋住。",
                     "페이지에 이미 인쇄된 내용(양식의 잘못된 데이터 등) 위로 상자를 드래그하세요. Washi 는 PDF 에서 실제로 삭제하며, 위를 덮기만 하지 않습니다.",
                     "Tracez des cadres sur ce qui est déjà imprimé (par exemple des données erronées sur un formulaire). Washi le supprime vraiment du PDF ; rien n’est simplement masqué.",
                     "Ziehen Sie Rahmen über bereits Gedrucktes, etwa falsche Angaben auf einem Formular. Washi löscht es wirklich aus dem PDF; nichts wird nur abgedeckt."],
    "Ui_EraseBoxes": ["BOXES", "枠", "框", "框", "상자", "CADRES", "RAHMEN"],
    "Ui_EraseNoBoxes": ["Drag on the page to draw a box.", "ページ上をドラッグして枠を描きます。", "在页面上拖动以画框。", "在頁面上拖曳以畫框。", "페이지 위를 드래그해 상자를 그리세요.", "Faites glisser sur la page pour tracer un cadre.", "Auf der Seite ziehen, um einen Rahmen zu zeichnen."],
    "Ui_EraseBoxLabel": ["Page {0} · {1} × {2} pt", "{0} ページ · {1} × {2} pt", "第 {0} 页 · {1} × {2} pt", "第 {0} 頁 · {1} × {2} pt", "{0}쪽 · {1} × {2} pt", "Page {0} · {1} × {2} pt", "Seite {0} · {1} × {2} pt"],
    "Ui_EraseClear": ["Clear boxes", "枠をすべて消す", "清除所有框", "清除所有框", "상자 모두 지우기", "Effacer les cadres", "Rahmen entfernen"],
    "Ui_ErasePlan": ["Will remove {0} text run(s), {1} image(s), {2} drawing(s).", "文字 {0}、画像 {1}、図形 {2} を削除します。", "将删除 {0} 段文字、{1} 个图像、{2} 个图形。", "將刪除 {0} 段文字、{1} 個影像、{2} 個圖形。", "글자 {0}개, 이미지 {1}개, 도형 {2}개를 삭제합니다.", "Supprimera {0} texte(s), {1} image(s), {2} dessin(s).", "Entfernt {0} Text(e), {1} Bild(er), {2} Zeichnung(en)."],
    "Ui_EraseKeptNote": ["{0} item(s) stay: {1}", "{0} 件は残ります: {1}", "{0} 项保留：{1}", "{0} 項保留：{1}", "{0}개는 남습니다: {1}", "{0} élément(s) restent : {1}", "{0} Element(e) bleiben: {1}"],
    "Ui_EraseLegendRemove": ["removed", "削除", "删除", "刪除", "삭제", "supprimé", "entfernt"],
    "Ui_EraseLegendKeep": ["stays", "残る", "保留", "保留", "유지", "conservé", "bleibt"],
    "Ui_EraseOptions": ["OPTIONS", "オプション", "选项", "選項", "옵션", "OPTIONS", "OPTIONEN"],
    "Ui_EraseKeepColored": ["Keep coloured printing", "色付きの印刷は残す", "保留彩色印刷", "保留彩色印刷", "색상 인쇄는 유지", "Garder l’impression en couleur", "Farbigen Druck behalten"],
    "Ui_EraseKeepColoredNote": ["Official forms print the form in colour and the data in black: only black and grey ink is erased.",
                                "官公庁の様式は様式を色で、データを黒で印刷します。黒とグレーの印字だけを消します。",
                                "官方表格用彩色印刷表格本身、用黑色印刷数据：只删除黑色和灰色的印字。",
                                "官方表單用彩色印刷表單本身、用黑色印刷資料：只刪除黑色和灰色的印字。",
                                "공식 양식은 양식을 색으로, 데이터를 검은색으로 인쇄합니다. 검은색과 회색만 지웁니다.",
                                "Les formulaires officiels impriment le formulaire en couleur et les données en noir : seule l’encre noire ou grise est effacée.",
                                "Amtliche Formulare drucken das Formular farbig und die Angaben schwarz: nur schwarze und graue Farbe wird gelöscht."],
    "Ui_EraseRules": ["Also erase thin lines (table rules, box dividers)", "細い線（罫線・枠の区切り）も消す", "也删除细线（表格线、格子分隔线）", "也刪除細線（表格線、格子分隔線）", "가는 선(표 선, 칸 구분선)도 지우기", "Effacer aussi les traits fins (filets, séparateurs de cases)", "Auch dünne Linien löschen (Tabellenlinien, Kästchentrenner)"],
    "Ui_EraseRun": ["Erase and save a copy", "消去してコピーを保存", "删除并另存副本", "刪除並另存副本", "지우고 사본 저장", "Effacer et enregistrer une copie", "Löschen und Kopie speichern"],
    "Ui_EraseRunNote": ["Writes <name>_erased.pdf next to the original and opens it. The original is not changed.",
                        "元のファイルの隣に <名前>_erased.pdf を書き出して開きます。元のファイルは変更しません。",
                        "在原文件旁写入 <名称>_erased.pdf 并打开。原文件不会被修改。",
                        "在原檔案旁寫入 <名稱>_erased.pdf 並開啟。原檔案不會被修改。",
                        "원본 옆에 <이름>_erased.pdf 를 만들고 엽니다. 원본은 바뀌지 않습니다.",
                        "Écrit <nom>_erased.pdf à côté de l’original et l’ouvre. L’original n’est pas modifié.",
                        "Schreibt <Name>_erased.pdf neben das Original und öffnet es. Das Original bleibt unverändert."],
    "Ui_EraseFailed": ["Could not erase: {0}", "消去できませんでした: {0}", "无法删除：{0}", "無法刪除：{0}", "지울 수 없습니다: {0}", "Effacement impossible : {0}", "Löschen nicht möglich: {0}"],
    "Ui_Erased": ["Saved {0}: {1} item(s) removed; check: {2} character(s) left in the erased areas, {3} image(s) dropped from the file.",
                  "{0} を保存しました: {1} 件を削除。確認: 消去した場所に残った文字 {2}、ファイルから除いた画像 {3}。",
                  "已保存 {0}：删除了 {1} 项；检查：删除处剩余文字 {2} 个，从文件中移除图像 {3} 个。",
                  "已儲存 {0}：刪除了 {1} 項；檢查：刪除處剩餘文字 {2} 個，從檔案中移除影像 {3} 個。",
                  "{0} 저장: {1}개 삭제. 확인: 지운 곳에 남은 글자 {2}개, 파일에서 뺀 이미지 {3}개.",
                  "{0} enregistré : {1} élément(s) supprimé(s) ; contrôle : {2} caractère(s) restant(s) dans les zones effacées, {3} image(s) retirée(s) du fichier.",
                  "{0} gespeichert: {1} Element(e) entfernt; Prüfung: {2} Zeichen in den gelöschten Bereichen übrig, {3} Bild(er) aus der Datei entfernt."],
    "Erase_KeptRunsOutside": ["a line of text runs outside the box: widen the box", "枠の外まで続く文字列があります。枠を広げてください", "有文字超出框外：请把框拉大", "有文字超出框外：請把框拉大", "상자 밖으로 이어지는 글자가 있습니다. 상자를 넓히세요", "une ligne de texte dépasse du cadre : agrandissez-le", "eine Textzeile reicht über den Rahmen hinaus: Rahmen vergrößern"],
    "Erase_KeptLargeImage": ["a large image (a scan?) covers this area; its pixels are not changed", "大きな画像（スキャン？）がこの範囲を覆っています。画素は変更しません", "这里被一张大图（扫描件？）覆盖，不会修改其像素", "這裡被一張大圖（掃描檔？）覆蓋，不會修改其像素", "큰 이미지(스캔?)가 이 영역을 덮고 있어 픽셀은 바꾸지 않습니다", "une grande image (un scan ?) couvre cette zone ; ses pixels ne sont pas modifiés", "ein großes Bild (Scan?) bedeckt diesen Bereich; seine Pixel bleiben"],
    "Erase_KeptGrouped": ["grouped content is not erased", "グループ化された内容は消去しません", "成组的内容不会删除", "成組的內容不會刪除", "그룹으로 묶인 내용은 지우지 않습니다", "le contenu groupé n’est pas effacé", "gruppierter Inhalt wird nicht gelöscht"],
    "Erase_KeptColored": ["coloured printing of the form is kept (option above)", "様式の色付き印刷は残します（上のオプション）", "表格的彩色印刷会保留（见上方选项）", "表單的彩色印刷會保留（見上方選項）", "양식의 색상 인쇄는 유지됩니다(위 옵션)", "l’impression en couleur du formulaire est conservée (option ci-dessus)", "farbiger Formulardruck bleibt (Option oben)"],
    "Erase_KeptClips": ["text that other content is clipped to is kept (erasing it would change what shows through)", "ほかの内容の切り抜きに使われている文字は残します（消すと見え方が変わります）", "用作其他内容裁剪形状的文字会保留（删除会改变显示效果）", "用作其他內容裁剪形狀的文字會保留（刪除會改變顯示效果）", "다른 내용을 오려 내는 데 쓰이는 글자는 유지합니다(지우면 보이는 모습이 바뀝니다)", "le texte servant de masque à d’autres contenus est conservé (l’effacer changerait ce qui apparaît)", "Text, der anderen Inhalt beschneidet, bleibt (Löschen würde das Sichtbare verändern)"],
    "Erase_KeptChained": ["a text run joined to the next one on its line: widen the box to include both", "同じ行の次の文字列とつながっています。両方を含むよう枠を広げてください", "与同一行的下一段文字相连：请把框拉大到包含两段", "與同一行的下一段文字相連：請把框拉大到包含兩段", "같은 줄의 다음 글자와 이어져 있습니다. 둘 다 들어가게 상자를 넓히세요", "texte lié au suivant sur sa ligne : agrandissez le cadre pour inclure les deux", "Text hängt am nächsten auf der Zeile: Rahmen so vergrößern, dass beide drin sind"],
    "Ui_AiModelWatermark": ["Choose or type a model", "モデルを選ぶか入力", "选择或输入模型", "選擇或輸入模型", "모델을 고르거나 입력", "Choisir ou saisir un modèle", "Modell wählen oder eingeben"],
    "Ui_AiModelsRefresh": ["Fetch models", "モデル一覧を取得", "获取模型列表", "取得模型清單", "모델 목록 가져오기", "Charger les modèles", "Modelle laden"],
    "Ui_AiModelsLoading": ["Fetching the provider's models…", "プロバイダのモデル一覧を取得中…", "正在获取提供方的模型列表…", "正在取得提供者的模型清單…", "제공자의 모델 목록을 가져오는 중…", "Chargement des modèles du fournisseur…", "Modelle des Anbieters werden geladen…"],
    "Ui_AiModelsLoaded": ["{0} models available: pick one from the list.", "利用できるモデル {0} 個。一覧から選んでください。", "可用模型 {0} 个，请从列表中选择。", "可用模型 {0} 個，請從清單中選擇。", "사용 가능한 모델 {0}개. 목록에서 고르세요.", "{0} modèles disponibles : choisissez-en un dans la liste.", "{0} Modelle verfügbar: eines aus der Liste wählen."],
    "Ui_AiModelsNotListed": ["{0} models available; '{1}' is not among them.", "利用できるモデル {0} 個。「{1}」は含まれていません。", "可用模型 {0} 个，其中没有“{1}”。", "可用模型 {0} 個，其中沒有「{1}」。", "사용 가능한 모델 {0}개. '{1}'은(는) 목록에 없습니다.", "{0} modèles disponibles ; « {1} » n’en fait pas partie.", "{0} Modelle verfügbar; „{1}“ ist nicht dabei."],
    "Ui_AiModelsNone": ["The provider listed no models; type the model id.", "プロバイダはモデルを返しませんでした。モデル ID を入力してください。", "提供方没有返回模型，请手动输入模型 ID。", "提供者沒有回傳模型，請手動輸入模型 ID。", "제공자가 모델을 돌려주지 않았습니다. 모델 ID 를 입력하세요.", "Le fournisseur n’a listé aucun modèle ; saisissez son identifiant.", "Der Anbieter hat keine Modelle gemeldet; Modell-ID eingeben."],
    "Ui_AiModelsNeedKey": ["Add the API key to see the provider's models.", "API キーを入れるとプロバイダのモデル一覧が表示されます。", "填入 API key 后即可显示提供方的模型列表。", "填入 API key 後即可顯示提供者的模型清單。", "API 키를 넣으면 제공자의 모델 목록이 보입니다.", "Ajoutez la clé d’API pour voir les modèles du fournisseur.", "API-Schlüssel eingeben, um die Modelle des Anbieters zu sehen."],
    "Ui_AiModelsNeedEndpoint": ["Enter the endpoint first, e.g. http://localhost:11434/v1", "先にエンドポイントを入力してください（例: http://localhost:11434/v1）", "请先填写接口地址，例如 http://localhost:11434/v1", "請先填寫端點，例如 http://localhost:11434/v1", "먼저 엔드포인트를 입력하세요(예: http://localhost:11434/v1)", "Saisissez d’abord le point d’accès, p. ex. http://localhost:11434/v1", "Zuerst den Endpunkt eingeben, z. B. http://localhost:11434/v1"],
    "Ui_AiModelsTimeout": ["no answer within 30 seconds", "30 秒以内に応答がありません", "30 秒内没有响应", "30 秒內沒有回應", "30초 안에 응답이 없습니다", "pas de réponse en 30 secondes", "keine Antwort innerhalb von 30 Sekunden"],
    "Ui_MovePrinted": ["Make printed text editable", "印字を編集できる値にする", "把已印文字变为可编辑", "把已印文字變為可編輯", "인쇄된 글자를 편집 가능하게", "Rendre le texte imprimé modifiable", "Gedruckten Text bearbeitbar machen"],
    "Ui_MovePrintedTip": ["Erase what is printed in the fields and put it into the fields as values you can edit", "記入欄に印字された内容を消去し、編集できる値として記入欄に入れます", "删除填写栏里已印的内容，并把它作为可编辑的值填入栏位", "刪除填寫欄裡已印的內容，並把它作為可編輯的值填入欄位", "입력란에 인쇄된 내용을 지우고 편집할 수 있는 값으로 넣습니다", "Efface ce qui est imprimé dans les champs et le place dans les champs comme valeurs modifiables", "Löscht den Aufdruck in den Feldern und setzt ihn als bearbeitbare Werte ein"],
    "Ui_MovePrintedNote": ["Washi reads what is printed in each field (OCR when the PDF's text cannot be read), puts it in as the field's value and removes the print from a copy (<name>_erased.pdf). Check the values in Preview.",
                           "各記入欄の印字を読み取り（PDF の文字が読めない場合は OCR）、記入欄の値に入れて、コピー（<名前>_erased.pdf）から印字を消去します。プレビューで値を確認してください。",
                           "Washi 会读取每个栏位里已印的内容（PDF 文字无法读取时用 OCR），填为栏位的值，并在副本（<名称>_erased.pdf）中删除原印字。请在预览中核对。",
                           "Washi 會讀取每個欄位裡已印的內容（PDF 文字無法讀取時用 OCR），填為欄位的值，並在副本（<名稱>_erased.pdf）中刪除原印字。請在預覽中核對。",
                           "각 입력란에 인쇄된 내용을 읽어(PDF 글자를 읽을 수 없으면 OCR) 입력란 값으로 넣고, 사본(<이름>_erased.pdf)에서 인쇄를 지웁니다. 미리보기에서 값을 확인하세요.",
                           "Washi lit ce qui est imprimé dans chaque champ (OCR si le texte du PDF est illisible), le met comme valeur du champ et efface l’impression dans une copie (<nom>_erased.pdf). Vérifiez les valeurs dans l’aperçu.",
                           "Washi liest den Aufdruck in jedem Feld (OCR, wenn der PDF-Text unlesbar ist), setzt ihn als Feldwert ein und löscht den Aufdruck in einer Kopie (<Name>_erased.pdf). Werte in der Vorschau prüfen."],
    "Ui_MovingPrinted": ["Reading and erasing the printed values…", "印字を読み取って消去しています…", "正在读取并删除已印内容…", "正在讀取並刪除已印內容…", "인쇄된 값을 읽고 지우는 중…", "Lecture et effacement des valeurs imprimées…", "Gedruckte Werte werden gelesen und gelöscht…"],
    "Ui_MovedPrinted": ["{0} field(s) now hold their printed text as editable values ({1} read by OCR: please check them). The print was erased in {2}; click a field to edit it.",
                        "{0} 個の記入欄に印字を編集できる値として入れました（{1} 個は OCR で読み取り。確認してください）。印字は {2} で消去しました。記入欄をクリックすると編集できます。",
                        "已把 {0} 个栏位的已印文字变为可编辑的值（其中 {1} 个由 OCR 识别，请核对）。原印字已在 {2} 中删除；点击栏位即可修改。",
                        "已把 {0} 個欄位的已印文字變為可編輯的值（其中 {1} 個由 OCR 辨識，請核對）。原印字已在 {2} 中刪除；點擊欄位即可修改。",
                        "입력란 {0}개의 인쇄된 글자를 편집 가능한 값으로 넣었습니다({1}개는 OCR, 확인하세요). 인쇄는 {2} 에서 지웠습니다. 입력란을 클릭해 고치세요.",
                        "{0} champ(s) contiennent maintenant leur texte imprimé comme valeur modifiable ({1} lu(s) par OCR : vérifiez-les). L’impression a été effacée dans {2} ; cliquez sur un champ pour le modifier.",
                        "{0} Feld(er) enthalten ihren Aufdruck jetzt als bearbeitbaren Wert ({1} per OCR gelesen: bitte prüfen). Der Aufdruck wurde in {2} gelöscht; zum Ändern ein Feld anklicken."],
    "Ui_EraseMakeEditable": ["Then put an editable field in each box", "消去後、各枠に編集できる記入欄を置く", "删除后在每个框里放一个可编辑的栏位", "刪除後在每個框裡放一個可編輯的欄位", "지운 뒤 각 상자에 편집 가능한 입력란 넣기", "Puis placer un champ modifiable dans chaque cadre", "Danach in jeden Rahmen ein bearbeitbares Feld setzen"],
    "Ui_EraseMakeEditableNote": ["The field holds the text that was printed there (OCR when needed), so you can change it in place.",
                                 "記入欄には元の印字（必要なら OCR で読み取り）が入るので、その場で書き換えられます。",
                                 "栏位里会填入原来印的文字（需要时用 OCR 识别），可以直接在原处修改。",
                                 "欄位裡會填入原來印的文字（需要時用 OCR 辨識），可以直接在原處修改。",
                                 "입력란에 원래 인쇄된 글자(필요하면 OCR)가 들어가 그 자리에서 고칠 수 있습니다.",
                                 "Le champ reprend le texte imprimé (par OCR si besoin) : modifiez-le sur place.",
                                 "Das Feld enthält den gedruckten Text (bei Bedarf per OCR), direkt an Ort und Stelle änderbar."],
    "Ui_ErasedEditable": ["Saved {0}: {1} item(s) erased; {2} box(es) now hold an editable field with the old text. Click one to change it.",
                          "{0} を保存しました: {1} 件を消去し、{2} 個の枠に元の文字が入った編集できる記入欄を置きました。クリックして書き換えてください。",
                          "已保存 {0}：删除了 {1} 项，{2} 个框里已放入带原文字的可编辑栏位。点击即可修改。",
                          "已儲存 {0}：刪除了 {1} 項，{2} 個框裡已放入帶原文字的可編輯欄位。點擊即可修改。",
                          "{0} 저장: {1}개를 지우고, 상자 {2}개에 원래 글자가 든 편집 가능한 입력란을 넣었습니다. 클릭해 고치세요.",
                          "{0} enregistré : {1} élément(s) effacé(s) ; {2} cadre(s) contiennent un champ modifiable avec l’ancien texte. Cliquez pour le changer.",
                          "{0} gespeichert: {1} Element(e) gelöscht; {2} Rahmen enthalten ein bearbeitbares Feld mit dem alten Text. Zum Ändern anklicken."],
    "Ui_MovePrintedField": ["Make this printed text editable", "この印字を編集できる値にする", "把这里的印字变为可编辑", "把這裡的印字變為可編輯", "이 인쇄 글자를 편집 가능하게", "Rendre ce texte imprimé modifiable", "Diesen Aufdruck bearbeitbar machen"],
    # ---- errors (the English Message of the exception stays for logs, the CLI and agents)
    "Err_AiNoModel": ["No model is chosen yet: pick one in Settings → AI model.", "モデルが選ばれていません。「設定 → AI モデル」で選んでください。", "还没有选择模型：请在“设置 → AI 模型”中选择。", "還沒有選擇模型：請在「設定 → AI 模型」中選擇。", "모델이 선택되지 않았습니다. '설정 → AI 모델'에서 고르세요.", "Aucun modèle choisi : sélectionnez-en un dans Réglages → Modèle d’IA.", "Noch kein Modell gewählt: unter Einstellungen → KI-Modell auswählen."],
    "Err_AiNoKey": ["No API key for {0}: add it in Settings → AI model (or set {1}).", "{0} の API キーがありません。「設定 → AI モデル」で追加してください（または {1} を設定）。", "没有 {0} 的 API key：请在“设置 → AI 模型”中填写（或设置 {1}）。", "沒有 {0} 的 API key：請在「設定 → AI 模型」中填寫（或設定 {1}）。", "{0} 의 API 키가 없습니다. '설정 → AI 모델'에서 추가하세요(또는 {1} 설정).", "Pas de clé d’API pour {0} : ajoutez-la dans Réglages → Modèle d’IA (ou définissez {1}).", "Kein API-Schlüssel für {0}: unter Einstellungen → KI-Modell eintragen (oder {1} setzen)."],
    "Err_AiNoEndpoint": ["A local / OpenAI-compatible provider needs an endpoint, e.g. http://localhost:11434/v1", "ローカル / OpenAI 互換のプロバイダにはエンドポイントが必要です（例: http://localhost:11434/v1）", "本地 / OpenAI 兼容的提供方需要填写接口地址，例如 http://localhost:11434/v1", "本機 / OpenAI 相容的提供者需要填寫端點，例如 http://localhost:11434/v1", "로컬 / OpenAI 호환 제공자는 엔드포인트가 필요합니다(예: http://localhost:11434/v1)", "Un fournisseur local / compatible OpenAI a besoin d’un point d’accès, p. ex. http://localhost:11434/v1", "Ein lokaler / OpenAI-kompatibler Anbieter braucht einen Endpunkt, z. B. http://localhost:11434/v1"],
    "Err_AiCutOff": ["The answer was cut off (output limit); try fewer pages or fields at once.", "回答が途中で切れました（出力上限）。ページや記入欄を減らしてお試しください。", "回答被截断（输出上限）；请减少一次处理的页数或栏位。", "回答被截斷（輸出上限）；請減少一次處理的頁數或欄位。", "답변이 잘렸습니다(출력 한도). 페이지나 입력란 수를 줄여 보세요.", "La réponse a été coupée (limite de sortie) ; essayez avec moins de pages ou de champs.", "Die Antwort wurde abgeschnitten (Ausgabelimit); weniger Seiten oder Felder auf einmal versuchen."],
    "Err_AiFiltered": ["The provider's content filter stopped the answer.", "プロバイダのコンテンツフィルタが回答を止めました。", "提供方的内容过滤器中止了回答。", "提供者的內容過濾器中止了回答。", "제공자의 콘텐츠 필터가 답변을 중단했습니다.", "Le filtre de contenu du fournisseur a interrompu la réponse.", "Der Inhaltsfilter des Anbieters hat die Antwort gestoppt."],
    "Err_AiNoVision": ["{0} is set up without page images; OCR needs a model that reads images.", "{0} はページ画像なしの設定です。OCR には画像を読めるモデルが必要です。", "{0} 设置为不发送页面图像；OCR 需要能读图的模型。", "{0} 設定為不傳送頁面影像；OCR 需要能讀圖的模型。", "{0} 은(는) 페이지 이미지 없이 설정되어 있습니다. OCR 에는 이미지를 읽는 모델이 필요합니다.", "{0} est configuré sans images de page ; l’OCR exige un modèle qui lit les images.", "{0} ist ohne Seitenbilder eingerichtet; OCR braucht ein Modell, das Bilder liest."],
    "Err_AiBadJson": ["The model did not answer in the expected format; try again.", "モデルの回答が想定した形式ではありません。もう一度お試しください。", "模型没有按预期格式回答，请重试。", "模型沒有按預期格式回答，請重試。", "모델이 예상한 형식으로 답하지 않았습니다. 다시 시도하세요.", "Le modèle n’a pas répondu au format attendu ; réessayez.", "Das Modell hat nicht im erwarteten Format geantwortet; bitte erneut versuchen."],
    "Err_AiDeclined": ["The model declined this request. {0}", "モデルがこのリクエストを断りました。{0}", "模型拒绝了这个请求。{0}", "模型拒絕了這個請求。{0}", "모델이 이 요청을 거절했습니다. {0}", "Le modèle a refusé cette demande. {0}", "Das Modell hat die Anfrage abgelehnt. {0}"],
    "Err_AiKeychainMacOnly": ["Keys can only be stored on macOS for now; set the environment variable {0} instead.", "キーを保存できるのは今のところ macOS だけです。代わりに環境変数 {0} を設定してください。", "目前只有 macOS 可以保存 key；请改为设置环境变量 {0}。", "目前只有 macOS 可以儲存 key；請改為設定環境變數 {0}。", "지금은 macOS 에서만 키를 저장할 수 있습니다. 대신 환경 변수 {0} 을(를) 설정하세요.", "Les clés ne peuvent être enregistrées que sur macOS pour l’instant ; définissez plutôt la variable {0}.", "Schlüssel lassen sich derzeit nur unter macOS speichern; stattdessen die Umgebungsvariable {0} setzen."],
    "Err_AiKeychain": ["The key could not be stored in the Keychain: {0}", "キーをキーチェーンに保存できませんでした: {0}", "无法把 key 保存到钥匙串：{0}", "無法把 key 儲存到鑰匙圈：{0}", "키를 키체인에 저장할 수 없습니다: {0}", "La clé n’a pas pu être enregistrée dans le trousseau : {0}", "Der Schlüssel konnte nicht im Schlüsselbund gespeichert werden: {0}"],
    "Err_AiAuth": ["The API key was refused or is missing", "API キーが拒否されたか、ありません", "API key 被拒绝或缺失", "API key 被拒絕或缺失", "API 키가 거부되었거나 없습니다", "Clé d’API refusée ou absente", "API-Schlüssel abgelehnt oder fehlt"],
    "Err_AiRateLimited": ["Too many requests: wait a moment and try again", "リクエストが多すぎます。少し待ってからやり直してください", "请求过于频繁：请稍后重试", "請求過於頻繁：請稍後重試", "요청이 너무 많습니다. 잠시 후 다시 시도하세요", "Trop de requêtes : patientez puis réessayez", "Zu viele Anfragen: kurz warten und erneut versuchen"],
    "Err_AiOverloaded": ["The provider is overloaded: try again in a moment", "プロバイダが混み合っています。しばらくしてからやり直してください", "提供方目前过载：请稍后重试", "提供者目前過載：請稍後重試", "제공자가 과부하 상태입니다. 잠시 후 다시 시도하세요", "Le fournisseur est surchargé : réessayez dans un instant", "Der Anbieter ist überlastet: gleich noch einmal versuchen"],
    "Err_AiInvalidRequest": ["The provider rejected the request (unknown model or unsupported option)", "プロバイダがリクエストを拒否しました（モデル名の誤りか未対応の設定）", "提供方拒绝了请求（模型不存在或不支持某个选项）", "提供者拒絕了請求（模型不存在或不支援某個選項）", "제공자가 요청을 거부했습니다(알 수 없는 모델 또는 지원하지 않는 옵션)", "Le fournisseur a rejeté la requête (modèle inconnu ou option non prise en charge)", "Der Anbieter hat die Anfrage abgelehnt (unbekanntes Modell oder nicht unterstützte Option)"],
    "Err_AiNetwork": ["The provider could not be reached (network, address or timeout)", "プロバイダに接続できません（ネットワーク、アドレス、タイムアウト）", "无法连接提供方（网络、地址或超时）", "無法連線提供者（網路、位址或逾時）", "제공자에 연결할 수 없습니다(네트워크, 주소 또는 시간 초과)", "Fournisseur injoignable (réseau, adresse ou délai dépassé)", "Der Anbieter ist nicht erreichbar (Netzwerk, Adresse oder Zeitüberschreitung)"],
    "Err_AiMalformed": ["The provider's answer could not be understood", "プロバイダの応答を解釈できませんでした", "无法解析提供方的回应", "無法解析提供者的回應", "제공자의 응답을 해석할 수 없습니다", "La réponse du fournisseur est incompréhensible", "Die Antwort des Anbieters war nicht verständlich"],
    "Err_OcrNoEngine": ["No on-device OCR engine: on macOS install the Xcode command line tools (xcode-select --install); elsewhere install tesseract.", "この端末で使える OCR エンジンがありません。macOS では Xcode コマンドラインツール（xcode-select --install）、それ以外では tesseract をインストールしてください。", "没有可用的本机 OCR 引擎：macOS 请安装 Xcode 命令行工具（xcode-select --install），其他系统请安装 tesseract。", "沒有可用的本機 OCR 引擎：macOS 請安裝 Xcode 命令列工具（xcode-select --install），其他系統請安裝 tesseract。", "기기에서 쓸 OCR 엔진이 없습니다. macOS 는 Xcode 명령줄 도구(xcode-select --install), 그 밖에는 tesseract 를 설치하세요.", "Aucun moteur d’OCR local : sur macOS, installez les outils en ligne de commande Xcode (xcode-select --install) ; ailleurs, installez tesseract.", "Keine lokale OCR-Engine: unter macOS die Xcode-Kommandozeilenwerkzeuge installieren (xcode-select --install), sonst tesseract."],
    "Err_OcrVisionUnavailable": ["Apple Vision OCR needs macOS with the Xcode command line tools (xcode-select --install).", "Apple Vision の OCR には、Xcode コマンドラインツール（xcode-select --install）を入れた macOS が必要です。", "Apple Vision OCR 需要 macOS 并安装 Xcode 命令行工具（xcode-select --install）。", "Apple Vision OCR 需要 macOS 並安裝 Xcode 命令列工具（xcode-select --install）。", "Apple Vision OCR 은 Xcode 명령줄 도구(xcode-select --install)가 설치된 macOS 가 필요합니다.", "L’OCR Apple Vision exige macOS avec les outils en ligne de commande Xcode (xcode-select --install).", "Apple Vision OCR braucht macOS mit den Xcode-Kommandozeilenwerkzeugen (xcode-select --install)."],
    "Err_OcrVisionCompile": ["The Vision OCR helper could not be built: {0}", "Vision OCR のヘルパーをビルドできませんでした: {0}", "无法编译 Vision OCR 辅助程序：{0}", "無法編譯 Vision OCR 輔助程式：{0}", "Vision OCR 도우미를 빌드할 수 없습니다: {0}", "L’assistant d’OCR Vision n’a pas pu être compilé : {0}", "Das Vision-OCR-Hilfsprogramm konnte nicht gebaut werden: {0}"],
    "Err_OcrTesseractMissing": ["tesseract was not found; install it (macOS: brew install tesseract tesseract-lang).", "tesseract が見つかりません。インストールしてください（macOS: brew install tesseract tesseract-lang）。", "未找到 tesseract；请安装（macOS：brew install tesseract tesseract-lang）。", "找不到 tesseract；請安裝（macOS：brew install tesseract tesseract-lang）。", "tesseract 를 찾을 수 없습니다. 설치하세요(macOS: brew install tesseract tesseract-lang).", "tesseract est introuvable ; installez-le (macOS : brew install tesseract tesseract-lang).", "tesseract wurde nicht gefunden; bitte installieren (macOS: brew install tesseract tesseract-lang)."],
    "Err_OcrTesseractLanguages": ["tesseract has none of the languages {0} installed (installed: {1}). macOS: brew install tesseract-lang", "tesseract に言語 {0} が入っていません（インストール済み: {1}）。macOS: brew install tesseract-lang", "tesseract 没有安装语言 {0}（已安装：{1}）。macOS：brew install tesseract-lang", "tesseract 沒有安裝語言 {0}（已安裝：{1}）。macOS：brew install tesseract-lang", "tesseract 에 언어 {0} 이(가) 설치되어 있지 않습니다(설치됨: {1}). macOS: brew install tesseract-lang", "tesseract n’a aucune des langues {0} (installées : {1}). macOS : brew install tesseract-lang", "tesseract hat keine der Sprachen {0} installiert (installiert: {1}). macOS: brew install tesseract-lang"],
    "Err_OcrFailed": ["{0} OCR failed: {1}", "{0} の OCR に失敗しました: {1}", "{0} OCR 失败：{1}", "{0} OCR 失敗：{1}", "{0} OCR 실패: {1}", "Échec de l’OCR {0} : {1}", "{0}-OCR fehlgeschlagen: {1}"],
    "Err_EraseNoArea": ["Draw a box first.", "先に枠を描いてください。", "请先画一个框。", "請先畫一個框。", "먼저 상자를 그리세요.", "Tracez d’abord un cadre.", "Zuerst einen Rahmen zeichnen."],
    "Err_EraseNothing": ["There is nothing printed to erase there (with the current options).", "そこには消去できる印字がありません（現在のオプションで）。", "那里没有可删除的已印内容（按当前选项）。", "那裡沒有可刪除的已印內容（依目前選項）。", "거기에는 지울 인쇄 내용이 없습니다(현재 옵션 기준).", "Rien d’imprimé à effacer à cet endroit (avec les options actuelles).", "Dort ist nichts Gedrucktes zu löschen (mit den aktuellen Optionen)."],
    "Err_EraseOnlyKept": ["Nothing could be erased: {0}", "何も消去できませんでした: {0}", "没有可以删除的内容：{0}", "沒有可以刪除的內容：{0}", "아무것도 지울 수 없었습니다: {0}", "Rien n’a pu être effacé : {0}", "Nichts konnte gelöscht werden: {0}"],
    "Err_EraseUnmatched": ["Page {0} is built in a way Washi cannot erase safely yet; nothing was changed.", "{0} ページは、Washi がまだ安全に消去できない構造です。何も変更していません。", "第 {0} 页的结构 Washi 暂时无法安全删除；未做任何修改。", "第 {0} 頁的結構 Washi 暫時無法安全刪除；未做任何修改。", "{0}쪽은 Washi 가 아직 안전하게 지울 수 없는 구조입니다. 아무것도 바꾸지 않았습니다.", "La page {0} est construite d’une façon que Washi ne sait pas encore effacer sans risque ; rien n’a été modifié.", "Seite {0} ist so aufgebaut, dass Washi sie noch nicht sicher löschen kann; nichts wurde geändert."],
    "Err_EraseChanged": ["The result differed from the original outside the erased areas ({0} pixels), so it was not kept.", "消去した範囲の外で元と違いが出たため（{0} 画素）、結果は破棄しました。", "结果在删除范围之外与原件不同（{0} 个像素），因此没有保留。", "結果在刪除範圍之外與原件不同（{0} 個像素），因此沒有保留。", "지운 영역 밖에서 원본과 달라져({0}픽셀) 결과를 보관하지 않았습니다.", "Le résultat différait de l’original hors des zones effacées ({0} pixels) ; il n’a pas été conservé.", "Das Ergebnis wich außerhalb der gelöschten Bereiche vom Original ab ({0} Pixel) und wurde verworfen."],
    "Err_Exists": ["{0} already exists; Washi never overwrites files.", "{0} はすでに存在します。Washi はファイルを上書きしません。", "{0} 已存在；Washi 从不覆盖文件。", "{0} 已存在；Washi 從不覆寫檔案。", "{0} 이(가) 이미 있습니다. Washi 는 파일을 덮어쓰지 않습니다.", "{0} existe déjà ; Washi n’écrase jamais de fichier.", "{0} existiert bereits; Washi überschreibt nie Dateien."],
    "Err_NoPage": ["Page {0} does not exist (the PDF has {1}).", "{0} ページはありません（この PDF は {1} ページ）。", "第 {0} 页不存在（该 PDF 共 {1} 页）。", "第 {0} 頁不存在（該 PDF 共 {1} 頁）。", "{0}쪽은 없습니다(이 PDF 는 {1}쪽).", "La page {0} n’existe pas (le PDF en a {1}).", "Seite {0} gibt es nicht (das PDF hat {1})."],
    "Err_CannotOpen": ["{0} could not be opened.", "{0} を開けませんでした。", "无法打开 {0}。", "無法開啟 {0}。", "{0} 을(를) 열 수 없습니다.", "Impossible d’ouvrir {0}.", "{0} konnte nicht geöffnet werden."],
    "Err_PageUnreadable": ["Page {0} could not be read.", "{0} ページを読み取れませんでした。", "无法读取第 {0} 页。", "無法讀取第 {0} 頁。", "{0}쪽을 읽을 수 없습니다.", "La page {0} est illisible.", "Seite {0} konnte nicht gelesen werden."],
    "Err_ProtectedChanges": ["{0} does not allow changes (its permissions forbid them); Washi does not remove protection.", "{0} は変更が許可されていません（権限設定）。Washi は保護を解除しません。", "{0} 不允许修改（权限设置禁止）；Washi 不会解除保护。", "{0} 不允許修改（權限設定禁止）；Washi 不會解除保護。", "{0} 은(는) 변경이 허용되지 않습니다(권한 설정). Washi 는 보호를 해제하지 않습니다.", "{0} n’autorise pas les modifications (ses permissions l’interdisent) ; Washi ne retire pas la protection.", "{0} erlaubt keine Änderungen (Berechtigungen); Washi entfernt keinen Schutz."],
    "Err_ProtectedReuse": ["{0} does not allow its pages to be reused (its permissions forbid it); Washi does not remove protection.", "{0} はページの再利用が許可されていません（権限設定）。Washi は保護を解除しません。", "{0} 不允许复用其页面（权限设置禁止）；Washi 不会解除保护。", "{0} 不允許重複使用其頁面（權限設定禁止）；Washi 不會解除保護。", "{0} 은(는) 페이지 재사용이 허용되지 않습니다(권한 설정). Washi 는 보호를 해제하지 않습니다.", "{0} n’autorise pas la réutilisation de ses pages (permissions) ; Washi ne retire pas la protection.", "{0} erlaubt nicht, seine Seiten weiterzuverwenden (Berechtigungen); Washi entfernt keinen Schutz."],
    "Err_Password": ["{0} is protected with a password; Washi does not remove protection.", "{0} はパスワードで保護されています。Washi は保護を解除しません。", "{0} 有密码保护；Washi 不会解除保护。", "{0} 有密碼保護；Washi 不會解除保護。", "{0} 은(는) 암호로 보호되어 있습니다. Washi 는 보호를 해제하지 않습니다.", "{0} est protégé par mot de passe ; Washi ne retire pas la protection.", "{0} ist passwortgeschützt; Washi entfernt keinen Schutz."],
    "Err_NothingToMerge": ["There is nothing to merge.", "結合するものがありません。", "没有可合并的文件。", "沒有可合併的檔案。", "합칠 파일이 없습니다.", "Rien à fusionner.", "Es gibt nichts zusammenzuführen."],
    "Err_RangeOutside": ["Page range {0} is outside 1-{1}.", "ページ範囲 {0} は 1〜{1} の外です。", "页码范围 {0} 超出了 1-{1}。", "頁碼範圍 {0} 超出了 1-{1}。", "페이지 범위 {0} 이(가) 1-{1} 밖입니다.", "La plage de pages {0} sort de 1-{1}.", "Seitenbereich {0} liegt außerhalb von 1-{1}."],
    "Err_BadRange": ["'{0}' is not a page range (use e.g. 1-3,5).", "「{0}」はページ範囲ではありません（例: 1-3,5）。", "“{0}”不是有效的页码范围（例如 1-3,5）。", "「{0}」不是有效的頁碼範圍（例如 1-3,5）。", "'{0}' 은(는) 페이지 범위가 아닙니다(예: 1-3,5).", "« {0} » n’est pas une plage de pages (p. ex. 1-3,5).", "„{0}“ ist kein Seitenbereich (z. B. 1-3,5)."],
    "Err_NoSignature": ["There is no signature to place.", "配置する署名がありません。", "没有要放置的签名。", "沒有要放置的簽名。", "배치할 서명이 없습니다.", "Il n’y a aucune signature à placer.", "Es gibt keine Unterschrift zum Platzieren."],
    "Err_NoComment": ["There is no comment to add.", "追加する注釈がありません。", "没有要添加的批注。", "沒有要加入的註解。", "추가할 주석이 없습니다.", "Il n’y a aucun commentaire à ajouter.", "Es gibt keinen Kommentar zum Hinzufügen."],
    "Err_NoPages": ["There are no pages to write.", "書き出すページがありません。", "没有要写入的页面。", "沒有要寫入的頁面。", "저장할 페이지가 없습니다.", "Il n’y a aucune page à écrire.", "Es gibt keine Seiten zum Schreiben."],
    "Err_BadRotation": ["A rotation of {0}° is not a multiple of 90.", "{0}° の回転は 90 の倍数ではありません。", "旋转 {0}° 不是 90 的倍数。", "旋轉 {0}° 不是 90 的倍數。", "{0}° 회전은 90의 배수가 아닙니다.", "Une rotation de {0}° n’est pas un multiple de 90.", "Eine Drehung um {0}° ist kein Vielfaches von 90."],
    "Err_BadRotateSpec": ["'{0}' is not a rotation (use e.g. 2:90,5:-90).", "「{0}」は回転の指定ではありません（例: 2:90,5:-90）。", "“{0}”不是有效的旋转设置（例如 2:90,5:-90）。", "「{0}」不是有效的旋轉設定（例如 2:90,5:-90）。", "'{0}' 은(는) 회전 지정이 아닙니다(예: 2:90,5:-90).", "« {0} » n’est pas une rotation (p. ex. 2:90,5:-90).", "„{0}“ ist keine Drehung (z. B. 2:90,5:-90)."],
    "Err_PageTwice": ["Page {0} of {1} is listed twice; each page can appear once.", "{1} の {0} ページが 2 回指定されています。各ページは 1 回だけ使えます。", "{1} 的第 {0} 页出现了两次；每页只能用一次。", "{1} 的第 {0} 頁出現了兩次；每頁只能用一次。", "{1}의 {0}페이지가 두 번 지정되었습니다. 각 페이지는 한 번만 쓸 수 있습니다.", "La page {0} de {1} figure deux fois ; chaque page ne peut apparaître qu’une fois.", "Seite {0} von {1} ist doppelt aufgeführt; jede Seite darf nur einmal vorkommen."],
    # ---- the field panel while filling
    "Ui_Value": ["Value", "値", "值", "值", "값", "Valeur", "Wert"],
    "Ui_Ticked": ["Ticked", "チェックあり", "已勾选", "已勾選", "체크됨", "Coché", "Angekreuzt"],
    "Ui_FilledOnPage": ["Filled in on this page: {0} of {1}", "このページで記入済み: {0} / {1}", "本页已填写：{0} / {1}", "本頁已填寫：{0} / {1}", "이 페이지에서 작성됨: {0} / {1}", "Remplis sur cette page : {0} sur {1}", "Auf dieser Seite ausgefüllt: {0} von {1}"],
    "Ui_FieldsOnPageCount": ["Fields on this page ({0})", "このページの記入欄（{0}）", "本页的填写栏（{0}）", "本頁的填寫欄（{0}）", "이 페이지의 입력란 ({0})", "Champs de cette page ({0})", "Felder auf dieser Seite ({0})"],
    "Ui_NothingFilled": ["Nothing filled in on this page yet. Click a field on the page and type.", "このページはまだ何も記入されていません。ページ上の記入欄をクリックして入力してください。", "本页还没有填写内容。点击页面上的栏位即可输入。", "本頁還沒有填寫內容。點擊頁面上的欄位即可輸入。", "이 페이지는 아직 작성된 내용이 없습니다. 페이지의 입력란을 클릭해 입력하세요.", "Rien de rempli sur cette page pour l’instant. Cliquez sur un champ de la page et saisissez.", "Auf dieser Seite ist noch nichts ausgefüllt. Ein Feld auf der Seite anklicken und tippen."],
    "Ui_NoFieldsOnPage": ["No fields on this page yet.", "このページにはまだ記入欄がありません。", "本页还没有填写栏。", "本頁還沒有填寫欄。", "이 페이지에는 아직 입력란이 없습니다.", "Aucun champ sur cette page pour l’instant.", "Auf dieser Seite gibt es noch keine Felder."],
    # ---- text style of a field's value
    "Ui_TextSize": ["Text size", "文字サイズ", "字号", "字級", "글자 크기", "Taille du texte", "Schriftgröße"],
    "Ui_SizeAuto": ["Fit the box", "枠に合わせる", "适应栏位", "符合欄位", "칸에 맞춤", "Ajuster au champ", "An Feld anpassen"],
    "Ui_TextColor": ["Colour", "色", "颜色", "顏色", "색", "Couleur", "Farbe"],
    "Ui_ColorBlack": ["Black", "黒", "黑色", "黑色", "검정", "Noir", "Schwarz"],
    "Ui_ColorBlue": ["Blue", "青", "蓝色", "藍色", "파랑", "Bleu", "Blau"],
    "Ui_ColorRed": ["Red", "赤", "红色", "紅色", "빨강", "Rouge", "Rot"],
    "Ui_ColorGreen": ["Green", "緑", "绿色", "綠色", "초록", "Vert", "Grün"],
    "Ui_ColorGrey": ["Grey", "グレー", "灰色", "灰色", "회색", "Gris", "Grau"],
    "Ui_TextFace": ["Typeface", "書体", "字体", "字體", "글꼴", "Police", "Schrift"],
    "Ui_FaceAuto": ["Form default", "様式の標準", "表单默认", "表單預設", "양식 기본", "Par défaut du formulaire", "Formularstandard"],
    "Ui_FaceSerif": ["Mincho / Song (serif)", "明朝体（セリフ）", "宋体 / 明朝（衬线）", "明體 / 宋體（襯線）", "명조 (세리프)", "Mincho / Song (serif)", "Mincho / Song (Serifen)"],
    "Ui_FaceSans": ["Gothic / Hei (sans-serif)", "ゴシック体（サンセリフ）", "黑体 / 哥特（无衬线）", "黑體 / 哥德（無襯線）", "고딕 (산세리프)", "Gothique / Hei (sans serif)", "Gothic / Hei (serifenlos)"],
    "Ui_RadioGroup": ["Group", "グループ", "分组", "群組", "그룹", "Groupe", "Gruppe"],
    "Ui_RadioGroupHint": ["Buttons with the same group form one choice: only one of them can be selected.", "同じグループのボタンは 1 つの選択肢になり、そのうち 1 つだけ選べます。", "同一分组的按钮构成一道单选题，只能选其中一个。", "同一群組的按鈕構成一道單選題，只能選其中一個。", "같은 그룹의 버튼은 하나의 선택지가 되며, 그중 하나만 고를 수 있습니다.", "Les boutons d’un même groupe forment un choix : un seul peut être sélectionné.", "Optionsfelder derselben Gruppe bilden eine Auswahl: nur eines davon kann gewählt sein."],
    "Ui_RadioExport": ["Option", "選択肢", "选项", "選項", "선택지", "Option", "Option"],
    "Ui_ChoiceOptions": ["Items (one per line)", "項目（1 行に 1 つ）", "选项（每行一个）", "選項（每行一個）", "항목 (한 줄에 하나)", "Éléments (un par ligne)", "Einträge (einer pro Zeile)"],
    "Ui_ChooseOption": ["Choose this option", "この選択肢を選ぶ", "选择这一项", "選擇這一項", "이 선택지 고르기", "Choisir cette option", "Diese Option wählen"],
    "Ui_SignHere": ["Sign here with the Sign tool.", "署名ツールでここに署名してください。", "请用签名工具在这里签名。", "請用簽名工具在這裡簽名。", "서명 도구로 여기에 서명하세요.", "Signez ici avec l’outil Signer.", "Hier mit dem Werkzeug „Unterschreiben“ unterschreiben."],
    "Ui_ToolSign": ["Sign", "署名", "签名", "簽名", "서명", "Signer", "Unterschreiben"],
    "Ui_SignTitle": ["Fill & Sign", "入力と署名", "填写并签名", "填寫並簽名", "작성 및 서명", "Remplir et signer", "Ausfüllen und unterschreiben"],
    "Ui_SignLead": ["Sign once below, then drag a box on the page (or pick a signature field) for each place to sign. A copy is saved with the signature in the page.",
                    "下に一度署名し、署名する場所ごとにページ上で枠をドラッグするか、署名欄を選んでください。署名をページに書き込んだコピーを保存します。",
                    "先在下方签一次名，然后在页面上为每个要签名的位置拖一个框（或选择签名栏）。保存时会生成一份签名已写进页面的副本。",
                    "先在下方簽一次名，然後在頁面上為每個要簽名的位置拖一個框（或選擇簽名欄）。儲存時會產生一份簽名已寫進頁面的副本。",
                    "아래에 한 번 서명한 뒤, 서명할 곳마다 페이지에서 상자를 끌거나 서명란을 고르세요. 서명이 페이지에 들어간 사본을 저장합니다.",
                    "Signez une fois ci-dessous, puis tracez un cadre sur la page (ou choisissez un champ de signature) pour chaque endroit à signer. Une copie est enregistrée avec la signature dans la page.",
                    "Unten einmal unterschreiben, dann für jede Stelle einen Rahmen auf der Seite ziehen (oder ein Unterschriftsfeld wählen). Eine Kopie mit der Unterschrift in der Seite wird gespeichert."],
    "Ui_SignPadHint": ["Sign here with a mouse, trackpad or pen", "マウス・トラックパッド・ペンでここに署名", "用鼠标、触控板或手写笔在这里签名", "用滑鼠、觸控板或手寫筆在這裡簽名", "마우스·트랙패드·펜으로 여기에 서명", "Signez ici à la souris, au pavé tactile ou au stylet", "Hier mit Maus, Trackpad oder Stift unterschreiben"],
    "Ui_SignClear": ["Clear", "消去", "清除", "清除", "지우기", "Effacer", "Löschen"],
    "Ui_SignRemember": ["Remember on this computer", "このコンピュータに保存", "保存在这台电脑上", "儲存在這台電腦上", "이 컴퓨터에 저장", "Mémoriser sur cet ordinateur", "Auf diesem Computer merken"],
    "Ui_SignSpots": ["Signature fields", "署名欄", "签名栏", "簽名欄", "서명란", "Champs de signature", "Unterschriftsfelder"],
    "Ui_SignSpot": ["{0} (page {1})", "{0}（{1} ページ）", "{0}（第 {1} 页）", "{0}（第 {1} 頁）", "{0} ({1}페이지)", "{0} (page {1})", "{0} (Seite {1})"],
    "Ui_SignHereButton": ["Sign here", "ここに署名", "在此签名", "在此簽名", "여기에 서명", "Signer ici", "Hier unterschreiben"],
    "Ui_SignPlacedTitle": ["Placed", "配置した署名", "已放置的签名", "已放置的簽名", "배치한 서명", "Signatures placées", "Platzierte Unterschriften"],
    "Ui_SignPlaced": ["Signature on page {0}", "{0} ページの署名", "第 {0} 页的签名", "第 {0} 頁的簽名", "{0}페이지 서명", "Signature page {0}", "Unterschrift auf Seite {0}"],
    "Ui_SignDrawFirst": ["Draw your signature in the panel first.", "先にパネルで署名を書いてください。", "请先在面板里签名。", "請先在面板裡簽名。", "먼저 패널에 서명하세요.", "Dessinez d’abord votre signature dans le panneau.", "Zuerst die Unterschrift im Bereich zeichnen."],
    "Ui_SignRun": ["Save signed copy…", "署名したコピーを保存…", "保存已签名的副本…", "儲存已簽名的副本…", "서명한 사본 저장…", "Enregistrer la copie signée…", "Unterschriebene Kopie speichern…"],
    "Ui_Signed": ["Saved {0} with {1} signature(s).", "{0} を保存しました（署名 {1} 件）。", "已保存 {0}（{1} 处签名）。", "已儲存 {0}（{1} 處簽名）。", "{0} 저장 (서명 {1}개).", "{0} enregistré avec {1} signature(s).", "{0} mit {1} Unterschrift(en) gespeichert."],
    "Ui_ToolComment": ["Comment", "注釈", "批注", "註解", "주석", "Commenter", "Kommentar"],
    "Ui_CommentTitle": ["Comment", "注釈を付ける", "添加批注", "加入註解", "주석 달기", "Commenter", "Kommentieren"],
    "Ui_CommentLead": ["Mark up text and add notes, text boxes, pen marks or boxes, then save a copy. Comments are standard PDF annotations that every viewer shows.",
                       "テキストへのマーク、メモ、テキストボックス、ペン、枠を追加してコピーを保存します。注釈は標準の PDF 注釈なので、どのビューアでも表示されます。",
                       "标记文字，添加便签、文本框、画笔或方框，然后另存一份。批注是标准 PDF 注释，任何阅读器都能显示。",
                       "標記文字，加入便箋、文字方塊、畫筆或方框，然後另存一份。註解是標準 PDF 註解，任何閱讀器都能顯示。",
                       "텍스트를 표시하고 메모, 텍스트 상자, 펜, 상자를 추가한 뒤 사본을 저장합니다. 주석은 표준 PDF 주석이라 어떤 뷰어에서나 보입니다.",
                       "Marquez du texte, ajoutez des notes, zones de texte, traits ou cadres, puis enregistrez une copie. Ce sont des annotations PDF standard, visibles dans tout lecteur.",
                       "Text markieren und Notizen, Textfelder, Stiftstriche oder Rahmen hinzufügen, dann eine Kopie speichern. Kommentare sind normale PDF-Anmerkungen, die jeder Viewer zeigt."],
    "Comment_Highlight": ["Highlight", "ハイライト", "高亮", "醒目提示", "형광펜", "Surligner", "Hervorheben"],
    "Comment_Underline": ["Underline", "下線", "下划线", "底線", "밑줄", "Souligner", "Unterstreichen"],
    "Comment_StrikeOut": ["Strike out", "取り消し線", "删除线", "刪除線", "취소선", "Barrer", "Durchstreichen"],
    "Comment_Note": ["Note", "メモ", "便签", "便箋", "메모", "Note", "Notiz"],
    "Comment_TextBox": ["Text box", "テキストボックス", "文本框", "文字方塊", "텍스트 상자", "Zone de texte", "Textfeld"],
    "Comment_Pen": ["Pen", "ペン", "画笔", "畫筆", "펜", "Crayon", "Stift"],
    "Comment_Box": ["Box", "枠", "方框", "方框", "상자", "Cadre", "Rahmen"],
    "Ui_CommentHintHighlight": ["Drag over the text to highlight it.", "ハイライトするテキストの上をドラッグします。", "在要高亮的文字上拖动。", "在要醒目提示的文字上拖曳。", "형광펜을 칠할 텍스트 위로 끄세요.", "Faites glisser sur le texte à surligner.", "Über den Text ziehen, der hervorgehoben werden soll."],
    "Ui_CommentHintUnderline": ["Drag over the text to underline it.", "下線を引くテキストの上をドラッグします。", "在要加下划线的文字上拖动。", "在要加底線的文字上拖曳。", "밑줄을 그을 텍스트 위로 끄세요.", "Faites glisser sur le texte à souligner.", "Über den Text ziehen, der unterstrichen werden soll."],
    "Ui_CommentHintStrikeOut": ["Drag over the text to strike it out.", "取り消し線を引くテキストの上をドラッグします。", "在要划掉的文字上拖动。", "在要劃掉的文字上拖曳。", "취소선을 그을 텍스트 위로 끄세요.", "Faites glisser sur le texte à barrer.", "Über den Text ziehen, der durchgestrichen werden soll."],
    "Ui_CommentHintNote": ["Type the note below, then click where it goes.", "下にメモを入力し、置く場所をクリックします。", "先在下面输入便签内容，再点击要放的位置。", "先在下面輸入便箋內容，再點選要放的位置。", "아래에 메모를 입력한 뒤 둘 곳을 클릭하세요.", "Saisissez la note ci-dessous, puis cliquez à l’endroit voulu.", "Notiz unten eingeben, dann auf die Stelle klicken."],
    "Ui_CommentHintTextBox": ["Type the text below, then drag a box (or click) on the page.", "下にテキストを入力し、ページ上で枠をドラッグ（またはクリック）します。", "先在下面输入文字，再在页面上拖一个框（或点击）。", "先在下面輸入文字，再在頁面上拖一個框（或點選）。", "아래에 텍스트를 입력한 뒤 페이지에서 상자를 끌거나 클릭하세요.", "Saisissez le texte ci-dessous, puis tracez un cadre (ou cliquez) sur la page.", "Text unten eingeben, dann auf der Seite einen Rahmen ziehen (oder klicken)."],
    "Ui_CommentHintPen": ["Draw on the page.", "ページに描きます。", "在页面上画。", "在頁面上畫。", "페이지에 그리세요.", "Dessinez sur la page.", "Auf der Seite zeichnen."],
    "Ui_CommentHintBox": ["Drag a box on the page.", "ページ上で枠をドラッグします。", "在页面上拖一个框。", "在頁面上拖一個框。", "페이지에서 상자를 끄세요.", "Tracez un cadre sur la page.", "Auf der Seite einen Rahmen ziehen."],
    "Ui_CommentText": ["Text", "テキスト", "文字", "文字", "텍스트", "Texte", "Text"],
    "Ui_CommentAuthor": ["Author (optional)", "作成者（任意）", "作者（可选）", "作者（選填）", "작성자 (선택)", "Auteur (facultatif)", "Verfasser (optional)"],
    "Ui_CommentNew": ["New comments", "新しい注釈", "新批注", "新註解", "새 주석", "Nouveaux commentaires", "Neue Kommentare"],
    "Ui_CommentExisting": ["Already in the file", "ファイルにある注釈", "文件里已有的批注", "檔案裡已有的註解", "파일에 있는 주석", "Déjà dans le fichier", "Bereits in der Datei"],
    "Ui_CommentItem": ["{0} · page {1}", "{0} · {1} ページ", "{0} · 第 {1} 页", "{0} · 第 {1} 頁", "{0} · {1}페이지", "{0} · page {1}", "{0} · Seite {1}"],
    "Ui_CommentNoText": ["There is no text under that box.", "その枠の下にテキストがありません。", "这个框下面没有文字。", "這個框下面沒有文字。", "그 상자 아래에 텍스트가 없습니다.", "Il n’y a pas de texte sous ce cadre.", "Unter diesem Rahmen ist kein Text."],
    "Ui_CommentTypeFirst": ["Type the text first.", "先にテキストを入力してください。", "请先输入文字。", "請先輸入文字。", "먼저 텍스트를 입력하세요.", "Saisissez d’abord le texte.", "Zuerst den Text eingeben."],
    "Ui_CommentRun": ["Save with comments…", "注釈付きで保存…", "保存带批注的副本…", "儲存含註解的副本…", "주석과 함께 저장…", "Enregistrer avec les commentaires…", "Mit Kommentaren speichern…"],
    "Ui_Commented": ["Saved {0} with {1} comment(s).", "{0} を保存しました（注釈 {1} 件）。", "已保存 {0}（{1} 条批注）。", "已儲存 {0}（{1} 則註解）。", "{0} 저장 (주석 {1}개).", "{0} enregistré avec {1} commentaire(s).", "{0} mit {1} Kommentar(en) gespeichert."],
    "Ui_StyleApplyAll": ["Use for all fields", "すべての記入欄に適用", "应用到所有栏位", "套用到所有欄位", "모든 입력란에 적용", "Appliquer à tous les champs", "Für alle Felder übernehmen"],
    "Ui_StyleApplied": ["Size, colour and typeface applied to {0} field(s).", "サイズ・色・書体を {0} 個の記入欄に適用しました。", "已把字号、颜色和字体应用到 {0} 个栏位。", "已把字級、顏色和字體套用到 {0} 個欄位。", "크기, 색, 글꼴을 입력란 {0}개에 적용했습니다.", "Taille, couleur et police appliquées à {0} champ(s).", "Größe, Farbe und Schrift auf {0} Feld(er) angewendet."],
    "Ui_ToolProtect": ["Protect", "保護", "保护", "保護", "보호", "Protéger", "Schützen"],
    "Ui_ProtectTitle": ["Protect", "保護", "保护", "保護", "보호", "Protéger", "Schützen"],
    "Ui_ProtectLead": ["Add a password, limit what readers can do, or remove information the pages don’t show. Each saves a copy.", "パスワードを付ける、閲覧者ができることを制限する、ページに表示されない情報を削除する。いずれもコピーを保存します。", "添加密码、限制读者能做的操作，或删除页面上看不到的信息。每项操作都会另存一份副本。", "加上密碼、限制讀者能做的操作，或刪除頁面上看不到的資訊。每項操作都會另存一份副本。", "암호를 추가하거나, 읽는 사람이 할 수 있는 일을 제한하거나, 페이지에 보이지 않는 정보를 삭제합니다. 모두 사본으로 저장됩니다.", "Ajoutez un mot de passe, limitez ce que les lecteurs peuvent faire ou supprimez les informations que les pages ne montrent pas. Chaque action enregistre une copie.", "Ein Passwort hinzufügen, einschränken, was Leser tun dürfen, oder Informationen entfernen, die die Seiten nicht zeigen. Jedes Mal wird eine Kopie gespeichert."],
    "Ui_ProtectLocked": ["{0} needs a password to open.", "{0} を開くにはパスワードが必要です。", "打开 {0} 需要密码。", "開啟 {0} 需要密碼。", "{0} 을(를) 열려면 암호가 필요합니다.", "{0} demande un mot de passe pour s’ouvrir.", "{0} benötigt zum Öffnen ein Passwort."],
    "Ui_ProtectPassword": ["Password", "パスワード", "密码", "密碼", "암호", "Mot de passe", "Passwort"],
    "Ui_ProtectOpen": ["Open", "開く", "打开", "開啟", "열기", "Ouvrir", "Öffnen"],
    "Ui_ProtectOpenNote": ["The password is kept in memory until Washi closes, never saved.", "パスワードは Washi を閉じるまでメモリにだけ保持され、保存されません。", "密码只保存在内存中，直到关闭 Washi，不会写入任何地方。", "密碼只保留在記憶體中，直到關閉 Washi，不會寫入任何地方。", "암호는 Washi 를 닫을 때까지 메모리에만 보관되며 저장되지 않습니다.", "Le mot de passe reste en mémoire jusqu’à la fermeture de Washi ; il n’est jamais enregistré.", "Das Passwort bleibt nur im Speicher, bis Washi geschlossen wird, und wird nie gespeichert."],
    "Ui_ProtectWrongPassword": ["That password is not right.", "パスワードが違います。", "密码不对。", "密碼不對。", "암호가 맞지 않습니다.", "Ce mot de passe n’est pas le bon.", "Das Passwort stimmt nicht."],
    "Ui_ProtectStateTitle": ["Current protection", "現在の保護", "当前保护", "目前保護", "현재 보호", "Protection actuelle", "Aktueller Schutz"],
    "Ui_ProtectNone": ["Not protected: anyone can open, copy and change it.", "保護なし：だれでも開いてコピー・変更できます。", "未加保护：任何人都能打开、复制和修改。", "未加保護：任何人都能開啟、複製和修改。", "보호 없음: 누구나 열고 복사하고 변경할 수 있습니다.", "Non protégé : tout le monde peut l’ouvrir, le copier et le modifier.", "Nicht geschützt: jeder kann es öffnen, kopieren und ändern."],
    "Ui_ProtectStateOpen": ["Needs a password to open ({0}).", "開くにはパスワードが必要です（{0}）。", "打开需要密码（{0}）。", "開啟需要密碼（{0}）。", "열려면 암호가 필요합니다 ({0}).", "Mot de passe requis pour l’ouvrir ({0}).", "Zum Öffnen ist ein Passwort nötig ({0})."],
    "Ui_ProtectStateRestricted": ["Opens without a password, but a permissions password limits what readers can do ({0}).", "パスワードなしで開けますが、権限パスワードで操作が制限されています（{0}）。", "无需密码即可打开，但权限密码限制了读者的操作（{0}）。", "無需密碼即可開啟，但權限密碼限制了讀者的操作（{0}）。", "암호 없이 열리지만 권한 암호로 할 수 있는 일이 제한됩니다 ({0}).", "S’ouvre sans mot de passe, mais un mot de passe d’autorisations limite ce que les lecteurs peuvent faire ({0}).", "Öffnet ohne Passwort, aber ein Berechtigungspasswort schränkt ein, was Leser tun dürfen ({0})."],
    "Ui_ProtectNotAllowed": ["Not allowed: {0}.", "許可されていない操作：{0}。", "不允许：{0}。", "不允許：{0}。", "허용되지 않음: {0}.", "Interdit : {0}.", "Nicht erlaubt: {0}."],
    "Ui_ProtectCurrentPassword": ["Permissions password (to change or remove)", "権限パスワード（変更・解除に必要）", "权限密码（修改或解除时需要）", "權限密碼（修改或解除時需要）", "권한 암호 (변경·해제에 필요)", "Mot de passe d’autorisations (pour modifier ou retirer)", "Berechtigungspasswort (zum Ändern oder Entfernen)"],
    "Ui_ProtectRemove": ["Save without password…", "パスワードなしで保存…", "另存为无密码副本…", "另存為無密碼副本…", "암호 없이 저장…", "Enregistrer sans mot de passe…", "Ohne Passwort speichern…"],
    "Ui_ProtectRemoved": ["Saved {0} without protection.", "{0} を保護なしで保存しました。", "已保存无保护的 {0}。", "已儲存無保護的 {0}。", "{0} 을(를) 보호 없이 저장했습니다.", "{0} enregistré sans protection.", "{0} ohne Schutz gespeichert."],
    "Ui_ProtectAddTitle": ["Add a password", "パスワードを付ける", "添加密码", "加上密碼", "암호 추가", "Ajouter un mot de passe", "Passwort hinzufügen"],
    "Ui_ProtectOpenPassword": ["Password to open (optional)", "開くときのパスワード（任意）", "打开密码（可选）", "開啟密碼（選填）", "열기 암호 (선택)", "Mot de passe d’ouverture (facultatif)", "Passwort zum Öffnen (optional)"],
    "Ui_ProtectRestrict": ["Limit printing, copying and changes", "印刷・コピー・変更を制限する", "限制打印、复制和修改", "限制列印、複製和修改", "인쇄·복사·변경 제한", "Limiter l’impression, la copie et les modifications", "Drucken, Kopieren und Ändern einschränken"],
    "Ui_ProtectPermissionsPassword": ["Permissions password", "権限パスワード", "权限密码", "權限密碼", "권한 암호", "Mot de passe d’autorisations", "Berechtigungspasswort"],
    "Ui_ProtectAllow": ["Readers may:", "閲覧者に許可する操作：", "允许读者：", "允許讀者：", "읽는 사람에게 허용:", "Les lecteurs peuvent :", "Leser dürfen:"],
    "Ui_PermPrint": ["Print", "印刷", "打印", "列印", "인쇄", "Imprimer", "Drucken"],
    "Ui_PermCopy": ["Copy text and images", "文字や画像のコピー", "复制文字和图像", "複製文字和影像", "글자와 이미지 복사", "Copier le texte et les images", "Text und Bilder kopieren"],
    "Ui_PermEdit": ["Change the content", "内容の変更", "修改内容", "修改內容", "내용 변경", "Modifier le contenu", "Inhalt ändern"],
    "Ui_PermComment": ["Add comments", "注釈の追加", "添加批注", "加入註解", "주석 추가", "Ajouter des commentaires", "Kommentare hinzufügen"],
    "Ui_PermFill": ["Fill in forms and sign", "フォーム入力と署名", "填写表单和签名", "填寫表單和簽名", "양식 작성과 서명", "Remplir les formulaires et signer", "Formulare ausfüllen und unterschreiben"],
    "Ui_PermAssemble": ["Insert, remove and turn pages", "ページの挿入・削除・回転", "插入、删除和旋转页面", "插入、刪除和旋轉頁面", "페이지 삽입·삭제·회전", "Insérer, supprimer et faire pivoter des pages", "Seiten einfügen, entfernen und drehen"],
    "Ui_ProtectShow": ["Show passwords", "パスワードを表示", "显示密码", "顯示密碼", "암호 표시", "Afficher les mots de passe", "Passwörter anzeigen"],
    "Ui_ProtectRun": ["Save protected copy…", "保護したコピーを保存…", "保存加密副本…", "儲存加密副本…", "보호된 사본 저장…", "Enregistrer une copie protégée…", "Geschützte Kopie speichern…"],
    "Ui_ProtectRunNote": ["AES-256 encryption. A forgotten password to open cannot be recovered: keep it safe.", "AES-256 で暗号化します。開くパスワードを忘れると元に戻せないので、大切に保管してください。", "使用 AES-256 加密。忘记打开密码将无法恢复，请妥善保管。", "使用 AES-256 加密。忘記開啟密碼將無法復原，請妥善保管。", "AES-256 으로 암호화합니다. 열기 암호를 잊으면 복구할 수 없으니 잘 보관하세요.", "Chiffrement AES-256. Un mot de passe d’ouverture oublié ne se récupère pas : conservez-le bien.", "AES-256-Verschlüsselung. Ein vergessenes Öffnungspasswort lässt sich nicht wiederherstellen: gut aufbewahren."],
    "Ui_Protected": ["Saved protected copy {0}.", "保護したコピー {0} を保存しました。", "已保存加密副本 {0}。", "已儲存加密副本 {0}。", "보호된 사본 {0} 저장.", "Copie protégée {0} enregistrée.", "Geschützte Kopie {0} gespeichert."],
    "Ui_HiddenTitle": ["Remove hidden information", "隠れた情報の削除", "删除隐藏信息", "刪除隱藏資訊", "숨은 정보 삭제", "Supprimer les informations masquées", "Versteckte Informationen entfernen"],
    "Ui_HiddenLead": ["Before sending a file: what it carries besides its pages.", "ファイルを送る前に：ページ以外に含まれている情報です。", "发送文件前检查：页面以外附带的信息。", "傳送檔案前檢查：頁面以外附帶的資訊。", "파일을 보내기 전에: 페이지 외에 담긴 정보입니다.", "Avant d’envoyer un fichier : ce qu’il contient en plus de ses pages.", "Vor dem Versenden: was die Datei außer ihren Seiten enthält."],
    "Ui_HiddenNothing": ["Nothing hidden found.", "隠れた情報は見つかりませんでした。", "没有发现隐藏信息。", "沒有發現隱藏資訊。", "숨은 정보가 없습니다.", "Aucune information masquée trouvée.", "Keine versteckten Informationen gefunden."],
    "Ui_HiddenProtected": ["Remove the password first, clean the copy, then protect it again.", "先にパスワードを外し、コピーを整理してから、もう一度保護してください。", "请先解除密码，清理副本后再重新加密。", "請先解除密碼，清理副本後再重新加密。", "먼저 암호를 해제하고 사본을 정리한 뒤 다시 보호하세요.", "Retirez d’abord le mot de passe, nettoyez la copie, puis protégez-la de nouveau.", "Zuerst das Passwort entfernen, die Kopie bereinigen und dann wieder schützen."],
    "Ui_HiddenMetadata": ["Document properties and metadata ({0})", "文書のプロパティとメタデータ（{0}）", "文档属性和元数据（{0}）", "文件內容與中繼資料（{0}）", "문서 속성과 메타데이터 ({0})", "Propriétés et métadonnées ({0})", "Dokumenteigenschaften und Metadaten ({0})"],
    "Ui_HiddenAttachments": ["Attached files ({0})", "添付ファイル（{0}）", "附件（{0}）", "附件（{0}）", "첨부 파일 ({0})", "Fichiers joints ({0})", "Angehängte Dateien ({0})"],
    "Ui_HiddenScripts": ["Scripts and actions ({0})", "スクリプトとアクション（{0}）", "脚本和动作（{0}）", "指令碼和動作（{0}）", "스크립트와 동작 ({0})", "Scripts et actions ({0})", "Skripte und Aktionen ({0})"],
    "Ui_HiddenComments": ["Comments ({0})", "注釈（{0}）", "批注（{0}）", "註解（{0}）", "주석 ({0})", "Commentaires ({0})", "Kommentare ({0})"],
    "Ui_HiddenPrivateData": ["Private program data and thumbnails ({0})", "アプリ固有のデータとサムネイル（{0}）", "程序私有数据和缩略图（{0}）", "程式私有資料和縮圖（{0}）", "프로그램 전용 데이터와 미리보기 ({0})", "Données privées de logiciels et vignettes ({0})", "Private Programmdaten und Miniaturen ({0})"],
    "Ui_HiddenEarlierVersions": ["Earlier versions kept in the file ({0}), always removed", "ファイルに残っている以前の版（{0}）：常に削除", "文件中保留的旧版本（{0}），总会删除", "檔案中保留的舊版本（{0}），一律刪除", "파일에 남은 이전 버전 ({0}), 항상 삭제", "Versions antérieures conservées dans le fichier ({0}), toujours supprimées", "In der Datei erhaltene frühere Versionen ({0}), werden immer entfernt"],
    "Ui_HiddenRun": ["Save cleaned copy…", "整理したコピーを保存…", "保存清理后的副本…", "儲存清理後的副本…", "정리한 사본 저장…", "Enregistrer une copie nettoyée…", "Bereinigte Kopie speichern…"],
    "Ui_Cleaned": ["Saved {0}: {1} hidden item(s) removed.", "{0} を保存しました（隠れた情報 {1} 件を削除）。", "已保存 {0}（删除 {1} 项隐藏信息）。", "已儲存 {0}（刪除 {1} 項隱藏資訊）。", "{0} 저장 (숨은 정보 {1}개 삭제).", "{0} enregistré : {1} élément(s) masqué(s) supprimé(s).", "{0} gespeichert: {1} versteckte(s) Element(e) entfernt."],
    "Err_NeedsPermissionsPassword": ["{0}: this needs its permissions password.", "{0}：この操作には権限パスワードが必要です。", "{0}：此操作需要权限密码。", "{0}：此操作需要權限密碼。", "{0}: 이 작업에는 권한 암호가 필요합니다.", "{0} : il faut son mot de passe d’autorisations.", "{0}: dafür ist das Berechtigungspasswort nötig."],
    "Err_NeedsPassword": ["{0} is protected with a password.", "{0} はパスワードで保護されています。", "{0} 有密码保护。", "{0} 有密碼保護。", "{0} 은(는) 암호로 보호되어 있습니다.", "{0} est protégé par un mot de passe.", "{0} ist mit einem Passwort geschützt."],
    "Err_WrongPassword": ["The password for {0} is not right.", "{0} のパスワードが違います。", "{0} 的密码不对。", "{0} 的密碼不對。", "{0} 의 암호가 맞지 않습니다.", "Le mot de passe de {0} n’est pas le bon.", "Das Passwort für {0} stimmt nicht."],
    "Err_ProtectNoPassword": ["Give a password to open, a permissions password, or both.", "開くパスワードか権限パスワード（または両方）を入力してください。", "请输入打开密码、权限密码，或两者都输入。", "請輸入開啟密碼、權限密碼，或兩者都輸入。", "열기 암호나 권한 암호(또는 둘 다)를 입력하세요.", "Indiquez un mot de passe d’ouverture, un mot de passe d’autorisations, ou les deux.", "Geben Sie ein Öffnungspasswort, ein Berechtigungspasswort oder beides an."],
    "Err_ProtectPermissionsPassword": ["Limits need a permissions password that differs from the password to open.", "制限には、開くパスワードとは別の権限パスワードが必要です。", "限制操作需要一个与打开密码不同的权限密码。", "限制操作需要一個與開啟密碼不同的權限密碼。", "제한하려면 열기 암호와 다른 권한 암호가 필요합니다.", "Les restrictions exigent un mot de passe d’autorisations différent du mot de passe d’ouverture.", "Einschränkungen brauchen ein Berechtigungspasswort, das sich vom Öffnungspasswort unterscheidet."],
    "Err_ProtectCheck": ["The copy did not check out, so it was not kept.", "コピーの確認に失敗したため、保存しませんでした。", "副本未通过检查，因此没有保留。", "副本未通過檢查，因此沒有保留。", "사본 확인에 실패하여 저장하지 않았습니다.", "La copie n’a pas passé la vérification ; elle n’a pas été conservée.", "Die Kopie hat die Prüfung nicht bestanden und wurde nicht behalten."],
    "Err_NotProtected": ["{0} is not protected.", "{0} は保護されていません。", "{0} 没有加保护。", "{0} 沒有加保護。", "{0} 은(는) 보호되어 있지 않습니다.", "{0} n’est pas protégé.", "{0} ist nicht geschützt."],
    "Err_CleanNothingChosen": ["Choose what to remove.", "削除するものを選んでください。", "请选择要删除的内容。", "請選擇要刪除的內容。", "삭제할 항목을 고르세요.", "Choisissez ce qu’il faut supprimer.", "Wählen Sie, was entfernt werden soll."],
    "Err_CleanProtected": ["{0} is protected: remove its password first, clean it, then protect it again.", "{0} は保護されています。先にパスワードを外して整理し、もう一度保護してください。", "{0} 有保护：请先解除密码并清理，再重新加密。", "{0} 有保護：請先解除密碼並清理，再重新加密。", "{0} 은(는) 보호되어 있습니다. 먼저 암호를 해제하고 정리한 뒤 다시 보호하세요.", "{0} est protégé : retirez d’abord son mot de passe, nettoyez-le, puis protégez-le de nouveau.", "{0} ist geschützt: zuerst das Passwort entfernen, bereinigen und dann wieder schützen."],
    "Err_CleanIncomplete": ["The cleaned copy still had {0}, so it was not kept.", "整理したコピーに {0} が残っていたため、保存しませんでした。", "清理后的副本仍有 {0}，因此没有保留。", "清理後的副本仍有 {0}，因此沒有保留。", "정리한 사본에 {0} 이(가) 남아 있어 저장하지 않았습니다.", "La copie nettoyée contenait encore {0} ; elle n’a pas été conservée.", "Die bereinigte Kopie enthielt noch {0} und wurde nicht behalten."],
    "Pdf_AskOpenPassword": ["Password to open:", "開くときのパスワード:", "打开密码：", "開啟密碼：", "열기 암호:", "Mot de passe d’ouverture :", "Passwort zum Öffnen:"],
    "Pdf_AskPermissionsPassword": ["Permissions password:", "権限パスワード:", "权限密码：", "權限密碼：", "권한 암호:", "Mot de passe d’autorisations :", "Berechtigungspasswort:"],
    "Pdf_AskPassword": ["Password for {0}:", "{0} のパスワード:", "{0} 的密码：", "{0} 的密碼：", "{0} 의 암호:", "Mot de passe de {0} :", "Passwort für {0}:"],
    "Pdf_Protected": ["Saved {0} (AES-256).", "{0} を保存しました（AES-256）。", "已保存 {0}（AES-256）。", "已儲存 {0}（AES-256）。", "{0} 저장 (AES-256).", "{0} enregistré (AES-256).", "{0} gespeichert (AES-256)."],
    "Pdf_Unlocked": ["Saved {0} without a password.", "{0} をパスワードなしで保存しました。", "已保存无密码的 {0}。", "已儲存無密碼的 {0}。", "{0} 을(를) 암호 없이 저장했습니다.", "{0} enregistré sans mot de passe.", "{0} ohne Passwort gespeichert."],
    "Ui_ToolRedact": ["Redact", "墨消し", "涂黑", "塗黑", "가리기", "Biffer", "Schwärzen"],
    "Ui_RedactTitle": ["Redact", "墨消し", "涂黑", "塗黑", "가리기", "Biffer", "Schwärzen"],
    "Ui_RedactLead": ["Find names, numbers or addresses, or drag over any part of a page. What lies under each black box is removed from the file for real, not just hidden. Saves a copy.", "氏名・番号・住所などを検索するか、ページ上をドラッグして範囲を指定します。黒い箱の下にあるものは、隠すだけでなくファイルから本当に削除されます。コピーとして保存します。", "查找姓名、号码或地址，或在页面上拖出要涂黑的区域。黑框下的内容会从文件里真正删除，而不只是遮住。另存为副本。", "搜尋姓名、號碼或地址，或在頁面上拖出要塗黑的區域。黑框下的內容會從檔案裡真正刪除，而不只是遮住。另存為副本。", "이름·번호·주소를 찾거나 페이지에서 원하는 부분을 끌어 지정합니다. 검은 상자 아래의 내용은 가려지기만 하는 것이 아니라 파일에서 실제로 삭제됩니다. 사본으로 저장합니다.", "Cherchez des noms, des numéros ou des adresses, ou faites glisser sur une partie de la page. Ce qui se trouve sous chaque bande noire est vraiment supprimé du fichier, pas seulement masqué. Enregistre une copie.", "Suchen Sie Namen, Nummern oder Adressen, oder ziehen Sie über einen Bereich der Seite. Was unter jedem schwarzen Balken liegt, wird wirklich aus der Datei entfernt, nicht nur verdeckt. Speichert eine Kopie."],
    "Ui_RedactFindTitle": ["Find", "検索", "查找", "搜尋", "찾기", "Rechercher", "Suchen"],
    "Ui_RedactWordsHint": ["Words or phrases, one per line", "語句（1 行に 1 つ）", "词或短语，每行一个", "詞或片語，每行一個", "단어나 구절 (한 줄에 하나)", "Mots ou expressions, un par ligne", "Wörter oder Ausdrücke, einer pro Zeile"],
    "Ui_RedactPatterns": ["Also find:", "あわせて検索：", "同时查找：", "同時搜尋：", "함께 찾기:", "Chercher aussi :", "Außerdem suchen:"],
    "Ui_RedactEmails": ["E-mail addresses", "メールアドレス", "电子邮件地址", "電子郵件地址", "이메일 주소", "Adresses e-mail", "E-Mail-Adressen"],
    "Ui_RedactPhones": ["Phone numbers", "電話番号", "电话号码", "電話號碼", "전화번호", "Numéros de téléphone", "Telefonnummern"],
    "Ui_RedactNumbers": ["ID, account and card numbers (7+ digits)", "番号（個人番号・口座・カードなど 7 桁以上）", "证件、账户和卡号（7 位以上数字）", "證件、帳戶和卡號（7 位以上數字）", "주민·계좌·카드 번호 등 (7자리 이상)", "Numéros d’identité, de compte, de carte (7 chiffres ou plus)", "Ausweis-, Konto- und Kartennummern (ab 7 Ziffern)"],
    "Ui_RedactMatchCase": ["Match case", "大文字と小文字を区別", "区分大小写", "區分大小寫", "대소문자 구분", "Respecter la casse", "Groß-/Kleinschreibung"],
    "Ui_RedactWholeWords": ["Whole words", "単語単位", "全字匹配", "全字相符", "단어 단위", "Mots entiers", "Ganze Wörter"],
    "Ui_RedactFind": ["Find", "検索", "查找", "搜尋", "찾기", "Rechercher", "Suchen"],
    "Ui_RedactFound": ["{0} found on {1} page(s). Untick what should stay.", "{1} ページで {0} 件見つかりました。残すものはチェックを外してください。", "在 {1} 页上找到 {0} 处。要保留的请取消勾选。", "在 {1} 頁上找到 {0} 處。要保留的請取消勾選。", "{1}쪽에서 {0}개를 찾았습니다. 남길 항목은 선택을 해제하세요.", "{0} trouvé(s) sur {1} page(s). Décochez ce qui doit rester.", "{0} Treffer auf {1} Seite(n). Entfernen Sie das Häkchen bei allem, was bleiben soll."],
    "Ui_RedactNoneFound": ["Nothing found. Scanned pages need OCR first so their text can be searched.", "見つかりませんでした。スキャンしたページは、先に OCR で文字を検索できるようにしてください。", "没有找到。扫描页需要先做 OCR 才能搜索文字。", "沒有找到。掃描頁需要先做 OCR 才能搜尋文字。", "찾지 못했습니다. 스캔한 페이지는 먼저 OCR 로 텍스트를 검색할 수 있게 하세요.", "Rien trouvé. Les pages numérisées doivent d’abord passer par l’OCR pour que leur texte soit cherchable.", "Nichts gefunden. Gescannte Seiten brauchen zuerst OCR, damit ihr Text durchsucht werden kann."],
    "Ui_RedactAll": ["All", "すべて", "全选", "全選", "모두", "Tout", "Alle"],
    "Ui_RedactNone": ["None", "なし", "全不选", "全不選", "없음", "Aucun", "Keine"],
    "Ui_RedactOnPage": ["Page {0}", "{0} ページ", "第 {0} 页", "第 {0} 頁", "{0}쪽", "Page {0}", "Seite {0}"],
    "Ui_RedactInField": ["Page {0}, form field (removed)", "{0} ページ・記入欄（削除）", "第 {0} 页，表单栏位（删除）", "第 {0} 頁，表單欄位（刪除）", "{0}쪽, 입력란 (삭제)", "Page {0}, champ de formulaire (supprimé)", "Seite {0}, Formularfeld (wird entfernt)"],
    "Ui_RedactInComment": ["Page {0}, comment (removed)", "{0} ページ・注釈（削除）", "第 {0} 页，批注（删除）", "第 {0} 頁，註解（刪除）", "{0}쪽, 주석 (삭제)", "Page {0}, commentaire (supprimé)", "Seite {0}, Kommentar (wird entfernt)"],
    "Ui_RedactAreasTitle": ["Areas", "範囲", "区域", "區域", "영역", "Zones", "Bereiche"],
    "Ui_RedactNoAreas": ["Drag on the page to black out any area: a photo, a signature, a stamp.", "ページ上をドラッグすると、写真・署名・印影など任意の範囲を墨消しできます。", "在页面上拖动，可以涂黑任意区域：照片、签名、印章。", "在頁面上拖曳，可以塗黑任意區域：照片、簽名、印章。", "페이지에서 끌면 사진·서명·도장 등 원하는 영역을 가릴 수 있습니다.", "Faites glisser sur la page pour biffer une zone : une photo, une signature, un tampon.", "Ziehen Sie auf der Seite, um einen Bereich zu schwärzen: ein Foto, eine Unterschrift, einen Stempel."],
    "Ui_RedactRun": ["Redact {0} and save copy…", "{0} 件を墨消しして保存…", "涂黑 {0} 处并另存副本…", "塗黑 {0} 處並另存副本…", "{0}개 가리고 사본 저장…", "Biffer {0} et enregistrer une copie…", "{0} schwärzen und Kopie speichern…"],
    "Ui_RedactRunNote": ["Text, image pixels and drawings under the boxes cannot be recovered from the copy. The same words are masked in document properties and bookmarks. The original is not changed.", "箱の下の文字・画像の画素・図形は、コピーから復元できません。文書のプロパティやしおりの同じ語句も伏せ字にします。元のファイルは変わりません。", "黑框下的文字、图像像素和图形无法从副本中恢复。文档属性和书签中的相同词语也会被遮盖。原文件不会改变。", "黑框下的文字、影像像素和圖形無法從副本中復原。文件屬性和書籤中的相同詞語也會被遮蓋。原檔案不會改變。", "상자 아래의 글자·이미지 픽셀·도형은 사본에서 복구할 수 없습니다. 문서 속성과 책갈피의 같은 단어도 가립니다. 원본은 바뀌지 않습니다.", "Le texte, les pixels d’image et les dessins sous les bandes ne peuvent pas être récupérés dans la copie. Les mêmes mots sont masqués dans les propriétés et les signets. L’original n’est pas modifié.", "Text, Bildpixel und Zeichnungen unter den Balken lassen sich aus der Kopie nicht wiederherstellen. Dieselben Wörter werden in Dokumenteigenschaften und Lesezeichen unkenntlich gemacht. Das Original bleibt unverändert."],
    "Ui_RedactNotOriginal": ["Save the redacted copy under another name: the original stays as it is.", "墨消ししたコピーは別の名前で保存してください（元のファイルはそのまま残します）。", "请用另一个文件名保存涂黑后的副本（原文件保持不变）。", "請用另一個檔名儲存塗黑後的副本（原檔案保持不變）。", "가린 사본은 다른 이름으로 저장하세요 (원본은 그대로 둡니다).", "Enregistrez la copie biffée sous un autre nom : l’original reste tel quel.", "Speichern Sie die geschwärzte Kopie unter einem anderen Namen: das Original bleibt, wie es ist."],
    "Ui_RedactFailed": ["Could not redact: {0}", "墨消しできませんでした：{0}", "无法涂黑：{0}", "無法塗黑：{0}", "가리지 못했습니다: {0}", "Biffage impossible : {0}", "Schwärzen nicht möglich: {0}"],
    "Ui_Redacted": ["Saved {0}: removed {1} character(s), {2} image area(s), {3} drawing(s), {4} other item(s). Checked: nothing found again, nothing moved.", "{0} を保存しました：文字 {1}、画像 {2}、図形 {3}、その他 {4} を削除。確認済み：再検索で見つからず、ほかの部分も変わっていません。", "已保存 {0}：删除文字 {1} 个、图像 {2} 处、图形 {3} 个、其他 {4} 项。已检查：再次搜索没有找到，其他部分也没有变化。", "已儲存 {0}：刪除文字 {1} 個、影像 {2} 處、圖形 {3} 個、其他 {4} 項。已檢查：再次搜尋沒有找到，其他部分也沒有變化。", "{0} 저장: 글자 {1}개, 이미지 {2}곳, 도형 {3}개, 기타 {4}개 삭제. 확인: 다시 찾아도 없고 다른 부분도 바뀌지 않았습니다.", "{0} enregistré : {1} caractère(s), {2} zone(s) d’image, {3} dessin(s), {4} autre(s) élément(s) supprimés. Vérifié : rien retrouvé, rien déplacé.", "{0} gespeichert: {1} Zeichen, {2} Bildbereich(e), {3} Zeichnung(en), {4} weitere Element(e) entfernt. Geprüft: nichts wiedergefunden, nichts verschoben."],
    "Err_RedactNothing": ["Nothing to redact: find something or drag an area first.", "墨消しするものがありません。先に検索するか範囲を指定してください。", "没有要涂黑的内容：请先查找或拖出区域。", "沒有要塗黑的內容：請先搜尋或拖出區域。", "가릴 항목이 없습니다. 먼저 찾거나 영역을 지정하세요.", "Rien à biffer : cherchez d’abord ou tracez une zone.", "Nichts zu schwärzen: zuerst suchen oder einen Bereich ziehen."],
    "Err_RedactNested": ["The page's content is nested too deeply to redact safely.", "ページの構造が深すぎるため、安全に墨消しできません。", "页面内容嵌套太深，无法安全涂黑。", "頁面內容巢狀太深，無法安全塗黑。", "페이지 구조가 너무 깊어 안전하게 가릴 수 없습니다.", "Le contenu de la page est trop imbriqué pour être biffé sans risque.", "Der Seiteninhalt ist zu tief verschachtelt, um ihn sicher zu schwärzen."],
    "Err_RedactInlineImage": ["A small embedded image lies partly under an area: draw the area over the whole image.", "小さな埋め込み画像が範囲に一部だけ重なっています。画像全体を囲むように範囲を指定してください。", "有一个内嵌小图像只有一部分在区域内：请让区域盖住整张图像。", "有一個內嵌小影像只有一部分在區域內：請讓區域蓋住整張影像。", "작은 내장 이미지가 영역에 일부만 겹칩니다. 이미지 전체를 덮도록 영역을 지정하세요.", "Une petite image intégrée n’est qu’en partie sous une zone : tracez la zone sur toute l’image.", "Ein kleines eingebettetes Bild liegt nur teilweise unter einem Bereich: ziehen Sie den Bereich über das ganze Bild."],
    "Err_RedactImage": ["An image under an area could not be read, so nothing was changed.", "範囲の下の画像を読み取れなかったため、何も変更しませんでした。", "区域下的图像无法读取，因此没有做任何修改。", "區域下的影像無法讀取，因此沒有做任何修改。", "영역 아래의 이미지를 읽을 수 없어 아무것도 바꾸지 않았습니다.", "Une image sous une zone n’a pas pu être lue ; rien n’a été modifié.", "Ein Bild unter einem Bereich ließ sich nicht lesen; nichts wurde geändert."],
    "Err_RedactRegex": ["Not a valid regular expression: {0}", "正規表現が正しくありません：{0}", "正则表达式无效：{0}", "規則運算式無效：{0}", "올바른 정규식이 아닙니다: {0}", "Expression régulière non valide : {0}", "Kein gültiger regulärer Ausdruck: {0}"],
    "Err_RedactRegexSlow": ["The regular expression took too long; make it simpler.", "正規表現の処理に時間がかかりすぎました。もっと単純にしてください。", "正则表达式耗时太长，请简化。", "規則運算式耗時太長，請簡化。", "정규식 처리가 너무 오래 걸립니다. 더 간단하게 하세요.", "L’expression régulière prend trop de temps ; simplifiez-la.", "Der reguläre Ausdruck dauert zu lange; vereinfachen Sie ihn."],
    "Err_RedactLeftover": ["{0} match(es) were still found in the redacted copy, so it was not kept.", "墨消ししたコピーにまだ {0} 件残っていたため、保存しませんでした。", "涂黑后的副本中仍找到 {0} 处，因此没有保留。", "塗黑後的副本中仍找到 {0} 處，因此沒有保留。", "가린 사본에서 아직 {0}개가 발견되어 저장하지 않았습니다.", "{0} occurrence(s) restaient dans la copie biffée ; elle n’a pas été conservée.", "In der geschwärzten Kopie wurden noch {0} Treffer gefunden; sie wurde nicht behalten."],
    "Err_RedactChanged": ["The redacted copy differed from the original outside the black boxes ({0} pixels), so it was not kept.", "墨消ししたコピーが黒い箱の外でも元と違っていた（{0} 画素）ため、保存しませんでした。", "涂黑后的副本在黑框以外与原文件不同（{0} 个像素），因此没有保留。", "塗黑後的副本在黑框以外與原檔案不同（{0} 個像素），因此沒有保留。", "가린 사본이 검은 상자 밖에서도 원본과 달라 ({0} 픽셀) 저장하지 않았습니다.", "La copie biffée différait de l’original hors des bandes noires ({0} pixels) ; elle n’a pas été conservée.", "Die geschwärzte Kopie wich außerhalb der Balken vom Original ab ({0} Pixel); sie wurde nicht behalten."],
    "Err_RedactUnstable": ["The redacted copy kept changing between checks, so it was not kept.", "確認のたびに結果が変わったため、墨消ししたコピーを保存しませんでした。", "每次检查结果都不同，因此没有保留涂黑后的副本。", "每次檢查結果都不同，因此沒有保留塗黑後的副本。", "확인할 때마다 결과가 달라져 가린 사본을 저장하지 않았습니다.", "La copie biffée changeait d’une vérification à l’autre ; elle n’a pas été conservée.", "Die geschwärzte Kopie änderte sich zwischen den Prüfungen; sie wurde nicht behalten."],
    "Pdf_Redacted": ["Saved {0}: removed {1} character(s), {2} image area(s), {3} drawing(s), {4} annotation(s) or field(s), {5} other text(s). Checked: nothing found again, nothing moved outside the boxes.", "{0} を保存しました：文字 {1}、画像 {2}、図形 {3}、注釈・記入欄 {4}、その他の文字列 {5} を削除。確認済み：再検索で見つからず、箱の外も変わっていません。", "已保存 {0}：删除文字 {1} 个、图像 {2} 处、图形 {3} 个、批注或栏位 {4} 个、其他文字 {5} 处。已检查：再次搜索没有找到，黑框外没有变化。", "已儲存 {0}：刪除文字 {1} 個、影像 {2} 處、圖形 {3} 個、註解或欄位 {4} 個、其他文字 {5} 處。已檢查：再次搜尋沒有找到，黑框外沒有變化。", "{0} 저장: 글자 {1}개, 이미지 {2}곳, 도형 {3}개, 주석·입력란 {4}개, 기타 문자열 {5}개 삭제. 확인: 다시 찾아도 없고 상자 밖은 바뀌지 않았습니다.", "{0} enregistré : {1} caractère(s), {2} zone(s) d’image, {3} dessin(s), {4} annotation(s) ou champ(s), {5} autre(s) texte(s) supprimés. Vérifié : rien retrouvé, rien changé hors des bandes.", "{0} gespeichert: {1} Zeichen, {2} Bildbereich(e), {3} Zeichnung(en), {4} Kommentar(e) oder Feld(er), {5} weitere(r) Text(e) entfernt. Geprüft: nichts wiedergefunden, außerhalb der Balken nichts verändert."],
    "Pdf_RedactMatch": ["  page {0} ({1}): {2}", "  {0} ページ（{1}）：{2}", "  第 {0} 页（{1}）：{2}", "  第 {0} 頁（{1}）：{2}", "  {0}쪽 ({1}): {2}", "  page {0} ({1}) : {2}", "  Seite {0} ({1}): {2}"],
    "Pdf_RedactFound": ["{0} match(es) on {1} page(s).", "{1} ページで {0} 件。", "共 {1} 页 {0} 处。", "共 {1} 頁 {0} 處。", "{1}쪽에서 {0}개.", "{0} occurrence(s) sur {1} page(s).", "{0} Treffer auf {1} Seite(n)."],
    "Ui_FindMenu": ["Find…", "検索…", "查找…", "尋找…", "찾기…", "Rechercher…", "Suchen…"],
    "Ui_FindNext": ["Find next", "次を検索", "查找下一个", "尋找下一個", "다음 찾기", "Suivant", "Weitersuchen"],
    "Ui_FindPrevious": ["Find previous", "前を検索", "查找上一个", "尋找上一個", "이전 찾기", "Précédent", "Rückwärts suchen"],
    "Ui_FindHint": ["Find in document", "文書内を検索", "在文档中查找", "在文件中尋找", "문서에서 찾기", "Rechercher dans le document", "Im Dokument suchen"],
    "Ui_FindNone": ["No results", "見つかりません", "没有结果", "沒有結果", "결과 없음", "Aucun résultat", "Keine Treffer"],
    "Ui_FindCount": ["{0} of {1}", "{0} / {1}", "{0} / {1}", "{0} / {1}", "{0} / {1}", "{0} sur {1}", "{0} von {1}"],
    "Pdf_FindCount": ["{0} hit(s) on {1} page(s).", "{1} ページで {0} 件。", "共 {1} 页 {0} 处。", "共 {1} 頁 {0} 處。", "{1}쪽에서 {0}개.", "{0} résultat(s) sur {1} page(s).", "{0} Treffer auf {1} Seite(n)."],
    "Pdf_FindHit": ["  page {0}: {1}", "  {0} ページ：{1}", "  第 {0} 页：{1}", "  第 {0} 頁：{1}", "  {0}쪽: {1}", "  page {0} : {1}", "  Seite {0}: {1}"],
    "Ui_BookmarksPanel": ["Bookmarks", "しおり", "书签", "書籤", "책갈피", "Signets", "Lesezeichen"],
    "Ui_BookmarkAdd": ["Add a bookmark for this page", "このページにしおりを追加", "为本页添加书签", "為本頁加入書籤", "이 페이지에 책갈피 추가", "Ajouter un signet pour cette page", "Lesezeichen für diese Seite hinzufügen"],
    "Ui_BookmarkRemove": ["Remove (with the bookmarks under it)", "削除（下位のしおりも）", "删除（连同下级书签）", "刪除（連同下層書籤）", "삭제 (하위 책갈피 포함)", "Supprimer (avec les signets en dessous)", "Entfernen (mit den Lesezeichen darunter)"],
    "Ui_BookmarkUp": ["Move up", "上へ", "上移", "上移", "위로", "Monter", "Nach oben"],
    "Ui_BookmarkDown": ["Move down", "下へ", "下移", "下移", "아래로", "Descendre", "Nach unten"],
    "Ui_BookmarkOutdent": ["Out of its parent", "1 段上の階層へ", "移到上一级", "移到上一層", "상위 수준으로", "Remonter d’un niveau", "Eine Ebene höher"],
    "Ui_BookmarkIndent": ["Under the bookmark above", "上のしおりの下へ", "移到上一个书签之下", "移到上一個書籤之下", "위 책갈피의 하위로", "Sous le signet du dessus", "Unter das Lesezeichen darüber"],
    "Ui_BookmarkTitle": ["Title", "タイトル", "标题", "標題", "제목", "Titre", "Titel"],
    "Ui_BookmarkUseThisPage": ["Point it to this page", "このページを指すようにする", "改为指向本页", "改為指向本頁", "이 페이지를 가리키게 하기", "Le faire pointer sur cette page", "Auf diese Seite zeigen lassen"],
    "Ui_BookmarksNone": ["This document has no bookmarks. Add one for the page you are reading with +.", "この文書にはしおりがありません。＋で表示中のページにしおりを追加できます。", "此文档没有书签。点 + 可为正在看的页面添加书签。", "此文件沒有書籤。按 + 可為正在看的頁面加入書籤。", "이 문서에는 책갈피가 없습니다. +로 보고 있는 페이지에 책갈피를 추가하세요.", "Ce document n’a pas de signets. Ajoutez-en un pour la page affichée avec +.", "Dieses Dokument hat keine Lesezeichen. Mit + fügen Sie eines für die angezeigte Seite hinzu."],
    "Ui_BookmarkPage": ["Page {0}", "{0} ページ", "第 {0} 页", "第 {0} 頁", "{0}쪽", "Page {0}", "Seite {0}"],
    "Ui_BookmarksSave": ["Save copy with bookmarks…", "しおり付きのコピーを保存…", "另存带书签的副本…", "另存帶書籤的副本…", "책갈피를 넣은 사본 저장…", "Enregistrer une copie avec les signets…", "Kopie mit Lesezeichen speichern…"],
    "Ui_BookmarksSaveNote": ["Bookmarks you did not change keep their links and style. The original is not changed.", "変更していないしおりはリンクと書式をそのまま保ちます。元のファイルは変わりません。", "没改动的书签保留原来的链接和样式。原文件不会改变。", "沒改動的書籤保留原本的連結和樣式。原檔案不會改變。", "바꾸지 않은 책갈피는 링크와 서식이 그대로 유지됩니다. 원본은 바뀌지 않습니다.", "Les signets non modifiés gardent leurs liens et leur style. L’original n’est pas modifié.", "Nicht geänderte Lesezeichen behalten Verknüpfung und Stil. Das Original bleibt unverändert."],
    "Ui_BookmarksNotOriginal": ["Save the copy under another name: the original stays as it is.", "コピーは別の名前で保存してください（元のファイルはそのまま残します）。", "请用另一个文件名保存副本（原文件保持不变）。", "請用另一個檔名儲存副本（原檔案保持不變）。", "사본은 다른 이름으로 저장하세요 (원본은 그대로 둡니다).", "Enregistrez la copie sous un autre nom : l’original reste tel quel.", "Speichern Sie die Kopie unter einem anderen Namen: das Original bleibt, wie es ist."],
    "Ui_BookmarksSaved": ["Saved {0} with {1} bookmark(s).", "{0} を保存しました（しおり {1} 件）。", "已保存 {0}（{1} 个书签）。", "已儲存 {0}（{1} 個書籤）。", "{0} 저장 (책갈피 {1}개).", "{0} enregistré avec {1} signet(s).", "{0} mit {1} Lesezeichen gespeichert."],
    "Err_BookmarkPage": ["Bookmark “{0}” points to page {1}, but the PDF has {2}.", "しおり「{0}」は {1} ページを指していますが、この PDF は {2} ページです。", "书签“{0}”指向第 {1} 页，但该 PDF 只有 {2} 页。", "書籤「{0}」指向第 {1} 頁，但該 PDF 只有 {2} 頁。", "책갈피 “{0}”가 {1}쪽을 가리키지만 이 PDF 는 {2}쪽입니다.", "Le signet « {0} » renvoie à la page {1}, mais le PDF en a {2}.", "Lesezeichen „{0}“ zeigt auf Seite {1}, das PDF hat aber {2}."],
    "Err_BookmarkTitle": ["Every bookmark needs a title.", "しおりにはタイトルが必要です。", "每个书签都需要标题。", "每個書籤都需要標題。", "모든 책갈피에는 제목이 필요합니다.", "Chaque signet a besoin d’un titre.", "Jedes Lesezeichen braucht einen Titel."],
    "Err_BookmarkCheck": ["The copy's bookmarks did not come out as asked, so it was not kept.", "コピーのしおりが指定どおりにならなかったため、保存しませんでした。", "副本的书签与要求不符，因此没有保留。", "副本的書籤與要求不符，因此沒有保留。", "사본의 책갈피가 지정한 대로 되지 않아 저장하지 않았습니다.", "Les signets de la copie ne correspondaient pas ; elle n’a pas été conservée.", "Die Lesezeichen der Kopie stimmten nicht; sie wurde nicht behalten."],
    "Pdf_BookmarksNone": ["No bookmarks.", "しおりはありません。", "没有书签。", "沒有書籤。", "책갈피가 없습니다.", "Aucun signet.", "Keine Lesezeichen."],
    "Pdf_BookmarksSaved": ["Saved {0} with {1} bookmark(s).", "{0} を保存しました（しおり {1} 件）。", "已保存 {0}（{1} 个书签）。", "已儲存 {0}（{1} 個書籤）。", "{0} 저장 (책갈피 {1}개).", "{0} enregistré avec {1} signet(s).", "{0} mit {1} Lesezeichen gespeichert."],
    "Ui_PrintMenu": ["Print…", "印刷…", "打印…", "列印…", "인쇄…", "Imprimer…", "Drucken…"],
    "Ui_PrintTitle": ["Print", "印刷", "打印", "列印", "인쇄", "Imprimer", "Drucken"],
    "Ui_PrintNote": ["Prints {0} as it is saved on disk.", "保存されている {0} を印刷します。", "打印磁盘上保存的 {0}。", "列印磁碟上儲存的 {0}。", "디스크에 저장된 {0} 을(를) 인쇄합니다.", "Imprime {0} tel qu’il est enregistré.", "Druckt {0}, wie es gespeichert ist."],
    "Ui_PrintPrinter": ["Printer", "プリンター", "打印机", "印表機", "프린터", "Imprimante", "Drucker"],
    "Ui_PrintCopies": ["Copies", "部数", "份数", "份數", "매수", "Copies", "Exemplare"],
    "Ui_PrintPages": ["Pages", "ページ", "页面", "頁面", "페이지", "Pages", "Seiten"],
    "Ui_PrintAll": ["All", "すべて", "全部", "全部", "전체", "Toutes", "Alle"],
    "Ui_PrintCurrentPage": ["Current page ({0})", "表示中のページ（{0}）", "当前页（第 {0} 页）", "目前頁（第 {0} 頁）", "현재 페이지 ({0})", "Page affichée ({0})", "Aktuelle Seite ({0})"],
    "Ui_PrintSome": ["Pages", "ページ指定", "指定页", "指定頁", "페이지 지정", "Pages", "Seiten"],
    "Ui_PrintTwoSided": ["Two-sided", "両面", "双面", "雙面", "양면", "Recto verso", "Beidseitig"],
    "Ui_PrintGray": ["Black and white", "白黒", "黑白", "黑白", "흑백", "Noir et blanc", "Schwarzweiß"],
    "Ui_PrintFit": ["Fit to paper", "用紙に合わせる", "适合纸张", "符合紙張", "용지에 맞춤", "Ajuster au papier", "An Papier anpassen"],
    "Ui_PrintActual": ["Actual size", "実際のサイズ", "实际大小", "實際大小", "실제 크기", "Taille réelle", "Originalgröße"],
    "Ui_PrintRun": ["Print", "印刷", "打印", "列印", "인쇄", "Imprimer", "Drucken"],
    "Ui_PrintNoPrinters": ["No printer is set up on this computer. Add one in the system settings, then try again.", "このコンピューターにはプリンターが設定されていません。システム設定で追加してから、もう一度お試しください。", "这台电脑没有设置打印机。请在系统设置中添加后再试。", "這台電腦沒有設定印表機。請在系統設定中加入後再試。", "이 컴퓨터에 설정된 프린터가 없습니다. 시스템 설정에서 추가한 뒤 다시 시도하세요.", "Aucune imprimante n’est configurée sur cet ordinateur. Ajoutez-en une dans les réglages du système, puis réessayez.", "Auf diesem Computer ist kein Drucker eingerichtet. Fügen Sie einen in den Systemeinstellungen hinzu und versuchen Sie es erneut."],
    "Ui_Printed": ["Sent {0} page(s) of {1} to {2}.", "{1} の {0} ページを {2} に送りました。", "已将 {1} 的 {0} 页发送到 {2}。", "已將 {1} 的 {0} 頁傳送到 {2}。", "{1} 의 {0}쪽을 {2} (으)로 보냈습니다.", "{0} page(s) de {1} envoyée(s) à {2}.", "{0} Seite(n) von {1} an {2} gesendet."],
    "Err_PrintMissing": ["{0} does not exist.", "{0} がありません。", "{0} 不存在。", "{0} 不存在。", "{0} 이(가) 없습니다.", "{0} n’existe pas.", "{0} existiert nicht."],
    "Err_PrintPassword": ["{0} needs a password to open, which the print system cannot give it. Save a copy without the password to open (Protect), then print that.", "{0} は開くのにパスワードが必要で、印刷システムでは開けません。「保護」で開くパスワードのないコピーを保存してから印刷してください。", "{0} 打开需要密码，打印系统无法打开它。请先在“保护”中另存一份无打开密码的副本，再打印。", "{0} 開啟需要密碼，列印系統無法開啟它。請先在「保護」中另存一份無開啟密碼的副本，再列印。", "{0} 은(는) 열 때 암호가 필요해 인쇄 시스템이 열 수 없습니다. '보호'에서 열기 암호 없는 사본을 저장한 뒤 인쇄하세요.", "{0} demande un mot de passe pour s’ouvrir, que le système d’impression ne peut pas fournir. Enregistrez une copie sans mot de passe d’ouverture (Protéger), puis imprimez-la.", "{0} braucht zum Öffnen ein Passwort, das das Drucksystem nicht hat. Speichern Sie unter Schützen eine Kopie ohne Öffnungspasswort und drucken Sie diese."],
    "Err_PrintNotAllowed": ["{0} does not allow printing.", "{0} は印刷が許可されていません。", "{0} 不允许打印。", "{0} 不允許列印。", "{0} 은(는) 인쇄가 허용되지 않습니다.", "{0} n’autorise pas l’impression.", "{0} erlaubt kein Drucken."],
    "Err_PrintCopies": ["Copies must be 1 to 999.", "部数は 1〜999 にしてください。", "份数必须是 1 到 999。", "份數必須是 1 到 999。", "매수는 1~999 여야 합니다.", "Le nombre de copies doit être compris entre 1 et 999.", "Die Anzahl muss zwischen 1 und 999 liegen."],
    "Err_PrintFailed": ["The print system refused the job: {0}", "印刷システムがジョブを受け付けませんでした：{0}", "打印系统拒绝了此任务：{0}", "列印系統拒絕了此工作：{0}", "인쇄 시스템이 작업을 거부했습니다: {0}", "Le système d’impression a refusé la tâche : {0}", "Das Drucksystem hat den Auftrag abgelehnt: {0}"],
    "Err_PrintNoSystem": ["No print system was found (CUPS: lp, lpstat).", "印刷システムが見つかりません（CUPS: lp, lpstat）。", "找不到打印系统（CUPS：lp、lpstat）。", "找不到列印系統（CUPS：lp、lpstat）。", "인쇄 시스템을 찾을 수 없습니다 (CUPS: lp, lpstat).", "Aucun système d’impression trouvé (CUPS : lp, lpstat).", "Kein Drucksystem gefunden (CUPS: lp, lpstat)."],
    "Err_PrintNoPrinter": ["There is no printer called {0}.", "{0} というプリンターはありません。", "没有名为 {0} 的打印机。", "沒有名為 {0} 的印表機。", "{0} (이)라는 프린터가 없습니다.", "Il n’y a pas d’imprimante nommée {0}.", "Es gibt keinen Drucker namens {0}."],
    "Pdf_PrintersNone": ["No printers.", "プリンターはありません。", "没有打印机。", "沒有印表機。", "프린터가 없습니다.", "Aucune imprimante.", "Keine Drucker."],
    "Pdf_PrinterDefault": ["{0} (default)", "{0}（既定）", "{0}（默认）", "{0}（預設）", "{0} (기본)", "{0} (par défaut)", "{0} (Standard)"],
    "Ui_FieldFormat": ["Format", "表示形式", "格式", "格式", "형식", "Format", "Format"],
    "Ui_FormatNone": ["None (any text)", "なし（自由入力）", "无（任意文字）", "無（任意文字）", "없음 (자유 입력)", "Aucun (texte libre)", "Keins (freier Text)"],
    "Ui_FormatNumber": ["Number", "数値", "数字", "數字", "숫자", "Nombre", "Zahl"],
    "Ui_FormatPercent": ["Percentage", "パーセント", "百分比", "百分比", "백분율", "Pourcentage", "Prozent"],
    "Ui_FormatDate": ["Date", "日付", "日期", "日期", "날짜", "Date", "Datum"],
    "Ui_FormatMask": ["Pattern (postal code, phone…)", "パターン（郵便番号・電話など）", "格式模板（邮编、电话等）", "格式範本（郵遞區號、電話等）", "패턴 (우편번호·전화 등)", "Masque (code postal, téléphone…)", "Muster (PLZ, Telefon …)"],
    "Ui_FieldDecimals": ["Decimals", "小数点以下の桁数", "小数位数", "小數位數", "소수 자릿수", "Décimales", "Nachkommastellen"],
    "Ui_FieldCurrency": ["Currency mark", "通貨記号", "货币符号", "貨幣符號", "통화 기호", "Symbole monétaire", "Währungszeichen"],
    "Ui_FieldPattern": ["Pattern", "パターン", "模板", "範本", "패턴", "Modèle", "Muster"],
    "Ui_FieldPatternHint": ["Dates: yyyy mm dd (yyyy/mm/dd, dd.mm.yyyy). Patterns: 9 a digit, A a letter, other characters as they are (999-9999).", "日付: yyyy mm dd（yyyy/mm/dd など）。パターン: 9 は数字、A は文字、その他はそのまま（999-9999）。", "日期：yyyy mm dd（如 yyyy/mm/dd）。模板：9 为数字，A 为字母，其他字符原样保留（999-9999）。", "日期：yyyy mm dd（如 yyyy/mm/dd）。範本：9 為數字，A 為字母，其他字元原樣保留（999-9999）。", "날짜: yyyy mm dd (yyyy/mm/dd 등). 패턴: 9 는 숫자, A 는 문자, 나머지는 그대로 (999-9999).", "Dates : yyyy mm dd (yyyy/mm/dd, dd.mm.yyyy). Masques : 9 un chiffre, A une lettre, le reste tel quel (999-9999).", "Datum: yyyy mm dd (yyyy/mm/dd, dd.mm.yyyy). Muster: 9 eine Ziffer, A ein Buchstabe, alles andere bleibt (999-9999)."],
    "Ui_FieldMin": ["Minimum", "最小値", "最小值", "最小值", "최솟값", "Minimum", "Minimum"],
    "Ui_FieldMax": ["Maximum", "最大値", "最大值", "最大值", "최댓값", "Maximum", "Maximum"],
    "Ui_FieldCalc": ["Calculated", "計算", "计算", "計算", "계산", "Calcul", "Berechnung"],
    "Ui_CalcNone": ["No (typed in)", "しない（手入力）", "不计算（手动填写）", "不計算（手動填寫）", "안 함 (직접 입력)", "Non (saisi)", "Nein (eingegeben)"],
    "Ui_CalcSum": ["Sum of", "合計", "合计", "合計", "합계", "Somme de", "Summe von"],
    "Ui_CalcProduct": ["Product of", "積", "乘积", "乘積", "곱", "Produit de", "Produkt von"],
    "Ui_CalcAverage": ["Average of", "平均", "平均值", "平均值", "평균", "Moyenne de", "Durchschnitt von"],
    "Ui_CalcMin": ["Smallest of", "最小", "最小值", "最小值", "최솟값", "Minimum de", "Kleinster Wert von"],
    "Ui_CalcMax": ["Largest of", "最大", "最大值", "最大值", "최댓값", "Maximum de", "Größter Wert von"],
    "Ui_FieldCalcSources": ["Fields", "対象の記入欄", "所用栏位", "所用欄位", "대상 입력란", "Champs", "Felder"],
    "Ui_FieldCalcHint": ["Field names, separated by commas. The value is worked out in Preview, in the saved PDF and in viewers such as Acrobat.", "記入欄の名前をカンマ区切りで。値はプレビュー・保存した PDF・Acrobat などのビューアで計算されます。", "栏位名称，用逗号分隔。数值会在预览、保存的 PDF 以及 Acrobat 等阅读器中计算。", "欄位名稱，以逗號分隔。數值會在預覽、儲存的 PDF 以及 Acrobat 等閱讀器中計算。", "입력란 이름을 쉼표로 구분. 값은 미리보기·저장한 PDF·Acrobat 등 뷰어에서 계산됩니다.", "Noms des champs, séparés par des virgules. La valeur est calculée dans l’aperçu, dans le PDF enregistré et dans les lecteurs comme Acrobat.", "Feldnamen, durch Kommas getrennt. Der Wert wird in der Vorschau, im gespeicherten PDF und in Programmen wie Acrobat berechnet."],
    "Err_FieldNotNumber": ["“{0}” is not a number.", "「{0}」は数値ではありません。", "“{0}”不是数字。", "「{0}」不是數字。", "“{0}” 은(는) 숫자가 아닙니다.", "« {0} » n’est pas un nombre.", "„{0}“ ist keine Zahl."],
    "Err_FieldNotDate": ["“{0}” is not a date.", "「{0}」は日付ではありません。", "“{0}”不是日期。", "「{0}」不是日期。", "“{0}” 은(는) 날짜가 아닙니다.", "« {0} » n’est pas une date.", "„{0}“ ist kein Datum."],
    "Err_FieldNotMask": ["“{0}” does not fit {1}.", "「{0}」は {1} の形になっていません。", "“{0}”不符合 {1}。", "「{0}」不符合 {1}。", "“{0}” 이(가) {1} 형식에 맞지 않습니다.", "« {0} » ne correspond pas à {1}.", "„{0}“ passt nicht zu {1}."],
    "Err_FieldRange": ["“{0}” is outside {2}.", "「{0}」は範囲（{2}）の外です。", "“{0}”超出范围（{2}）。", "「{0}」超出範圍（{2}）。", "“{0}” 이(가) 범위({2})를 벗어났습니다.", "« {0} » est hors de {2}.", "„{0}“ liegt außerhalb von {2}."],
    "Err_FieldCalcCycle": ["The calculation of {0} depends on itself.", "{0} の計算が自分自身に依存しています。", "{0} 的计算依赖它自己。", "{0} 的計算依賴它自己。", "{0} 의 계산이 자기 자신에 의존합니다.", "Le calcul de {0} dépend de lui-même.", "Die Berechnung von {0} hängt von sich selbst ab."],
    "Ui_CommentExistingHint": ["Click one (here or on the page) to change its words or colour, remove it or reply; drag a note, box or drawing to move it.", "一覧かページ上でクリックすると、文字や色の変更・削除・返信ができます。メモ・枠・手書きはドラッグで移動できます。", "在列表或页面上点一下批注，就能改文字、换颜色、删除或回复；便签、方框和手绘可以拖动移动。", "在清單或頁面上點一下註解，就能改文字、換顏色、刪除或回覆；便箋、方框和手繪可以拖曳移動。", "목록이나 페이지에서 클릭하면 글자·색 변경, 삭제, 답글이 가능합니다. 메모·상자·펜 그림은 끌어서 옮길 수 있습니다.", "Cliquez sur un commentaire (ici ou sur la page) pour changer ses mots ou sa couleur, le supprimer ou y répondre ; faites glisser une note, un cadre ou un dessin pour le déplacer.", "Klicken Sie einen Kommentar an (hier oder auf der Seite), um Text oder Farbe zu ändern, ihn zu löschen oder zu beantworten; Notizen, Rahmen und Zeichnungen lassen sich verschieben."],
    "Ui_CommentMoveHint": ["Drag it on the page to move it.", "ページ上でドラッグすると移動できます。", "在页面上拖动即可移动。", "在頁面上拖曳即可移動。", "페이지에서 끌어서 옮길 수 있습니다.", "Faites-le glisser sur la page pour le déplacer.", "Zum Verschieben auf der Seite ziehen."],
    "Ui_CommentReply": ["Reply", "返信", "回复", "回覆", "답글", "Répondre", "Antworten"],
    "Ui_CommentReplyHint": ["Write a reply", "返信を入力", "写回复", "寫回覆", "답글 입력", "Écrire une réponse", "Antwort schreiben"],
    "Ui_CommentReplies": ["{0} repl(ies)", "返信 {0}", "{0} 条回复", "{0} 則回覆", "답글 {0}", "{0} réponse(s)", "{0} Antwort(en)"],
    "Ui_CommentDelete": ["Remove / keep", "削除 / 元に戻す", "删除 / 保留", "刪除 / 保留", "삭제 / 유지", "Supprimer / garder", "Entfernen / behalten"],
    "Ui_CommentRevert": ["Undo changes", "変更を取り消す", "撤销修改", "復原修改", "변경 취소", "Annuler les changements", "Änderungen verwerfen"],
    "Ui_CommentsSaved": ["Saved {0}: {1} new, {2} changed, {3} removed, {4} repl(ies).", "{0} を保存しました：追加 {1}、変更 {2}、削除 {3}、返信 {4}。", "已保存 {0}：新增 {1}、修改 {2}、删除 {3}、回复 {4}。", "已儲存 {0}：新增 {1}、修改 {2}、刪除 {3}、回覆 {4}。", "{0} 저장: 추가 {1}, 변경 {2}, 삭제 {3}, 답글 {4}.", "{0} enregistré : {1} nouveau(x), {2} modifié(s), {3} supprimé(s), {4} réponse(s).", "{0} gespeichert: {1} neu, {2} geändert, {3} entfernt, {4} Antwort(en)."],
    "Annot_Highlight": ["Highlight", "ハイライト", "高亮", "醒目提示", "강조", "Surlignage", "Hervorhebung"],
    "Annot_Underline": ["Underline", "下線", "下划线", "底線", "밑줄", "Soulignement", "Unterstreichung"],
    "Annot_StrikeOut": ["Strike-out", "取り消し線", "删除线", "刪除線", "취소선", "Barré", "Durchstreichung"],
    "Annot_Squiggly": ["Squiggly", "波線", "波浪线", "波浪線", "물결선", "Ondulé", "Wellenlinie"],
    "Annot_Text": ["Note", "メモ", "便签", "便箋", "메모", "Note", "Notiz"],
    "Annot_FreeText": ["Text box", "テキストボックス", "文本框", "文字方塊", "텍스트 상자", "Zone de texte", "Textfeld"],
    "Annot_Ink": ["Pen", "手書き", "手绘", "手繪", "펜", "Crayon", "Stift"],
    "Annot_Square": ["Box", "枠", "方框", "方框", "상자", "Cadre", "Rahmen"],
    "Annot_Circle": ["Circle", "円", "圆", "圓", "원", "Cercle", "Kreis"],
    "Annot_Line": ["Line", "線", "线条", "線條", "선", "Ligne", "Linie"],
    "Annot_Stamp": ["Stamp", "スタンプ", "图章", "圖章", "스탬프", "Tampon", "Stempel"],
    "Err_NoSuchComment": ["There is no comment “{0}”.", "コメント「{0}」はありません。", "没有批注“{0}”。", "沒有註解「{0}」。", "주석 “{0}” 이(가) 없습니다.", "Il n’y a pas de commentaire « {0} ».", "Es gibt keinen Kommentar „{0}“."],
    "Err_CommentNotRedrawable": ["Comment “{0}” was made by another program; Washi can change its words, remove it or answer it, but not its colour.", "コメント「{0}」は別のアプリで作られたため、文字の変更・削除・返信はできますが、色は変えられません。", "批注“{0}”由其他程序创建：可以改文字、删除或回复，但不能换颜色。", "註解「{0}」由其他程式建立：可以改文字、刪除或回覆，但不能換顏色。", "주석 “{0}” 은(는) 다른 프로그램에서 만들어 글자 변경·삭제·답글만 가능하고 색은 바꿀 수 없습니다.", "Le commentaire « {0} » vient d’un autre programme : Washi peut changer ses mots, le supprimer ou y répondre, mais pas sa couleur.", "Kommentar „{0}“ stammt aus einem anderen Programm: Text ändern, löschen oder beantworten geht, die Farbe nicht."],
    "Err_CommentNotMovable": ["Comment “{0}” cannot be moved (text markup stays on its text).", "コメント「{0}」は移動できません（文字への注釈は文字に付いたままです）。", "批注“{0}”不能移动（文字标记跟随文字）。", "註解「{0}」不能移動（文字標記跟隨文字）。", "주석 “{0}” 은(는) 옮길 수 없습니다 (글자 표시는 글자에 붙어 있습니다).", "Le commentaire « {0} » ne peut pas être déplacé (un marquage reste sur son texte).", "Kommentar „{0}“ lässt sich nicht verschieben (Textmarkierungen bleiben am Text)."],
    "Err_CommentCheck": ["The changed comments did not come out as asked, so the copy was not kept.", "変更したコメントが指定どおりにならなかったため、コピーを保存しませんでした。", "修改后的批注与要求不符，因此没有保留副本。", "修改後的註解與要求不符，因此沒有保留副本。", "변경한 주석이 지정한 대로 되지 않아 사본을 저장하지 않았습니다.", "Les commentaires modifiés ne correspondaient pas ; la copie n’a pas été conservée.", "Die geänderten Kommentare stimmten nicht; die Kopie wurde nicht behalten."],
    "Ui_ToolCompress": ["Compress", "圧縮", "压缩", "壓縮", "압축", "Compresser", "Verkleinern"],
    "Ui_CompressTitle": ["Compress", "ファイルを小さく", "压缩文件", "壓縮檔案", "파일 압축", "Compresser", "Verkleinern"],
    "Ui_CompressLead": ["Make a smaller copy for e-mail or upload. Pages look the same; each copy is compared with the original, page by page, before it is kept.", "メール送信やアップロード用に小さなコピーを作ります。見た目は変わりません。保存前に元のファイルとページごとに比べて確認します。", "生成一份更小的副本，方便发邮件或上传。页面外观不变；保存前会逐页与原文件比对。", "產生一份更小的副本，方便寄信或上傳。頁面外觀不變；儲存前會逐頁與原檔案比對。", "메일이나 업로드용으로 더 작은 사본을 만듭니다. 보이는 모습은 그대로이며, 저장 전에 원본과 페이지별로 비교합니다.", "Crée une copie plus légère pour l’e-mail ou le dépôt en ligne. Les pages restent identiques ; chaque copie est comparée à l’original, page par page, avant d’être conservée.", "Erstellt eine kleinere Kopie zum Mailen oder Hochladen. Die Seiten sehen gleich aus; jede Kopie wird vor dem Speichern Seite für Seite mit dem Original verglichen."],
    "Ui_CompressSize": ["Now: {0}", "現在: {0}", "当前：{0}", "目前：{0}", "현재: {0}", "Actuellement : {0}", "Jetzt: {0}"],
    "Ui_CompressLight": ["Light", "軽め", "轻度", "輕度", "가볍게", "Léger", "Leicht"],
    "Ui_CompressLightNote": ["Lossless clean-up; only images far more detailed than any screen or printer needs (above 300 dpi) are made smaller.", "画質を落とさない整理のみ。画面や印刷に必要以上の高解像度（300 dpi 超）の画像だけ小さくします。", "只做无损整理；仅缩小远超屏幕和打印所需的高分辨率图像（300 dpi 以上）。", "只做無損整理；僅縮小遠超螢幕和列印所需的高解析度影像（300 dpi 以上）。", "무손실 정리만 하며, 화면·인쇄에 필요 이상인 고해상도(300 dpi 초과) 이미지만 줄입니다.", "Nettoyage sans perte ; seules les images bien trop détaillées (plus de 300 dpi) sont allégées.", "Verlustfreies Aufräumen; nur Bilder mit weit mehr Auflösung als nötig (über 300 dpi) werden verkleinert."],
    "Ui_CompressStandard": ["Standard", "標準", "标准", "標準", "표준", "Standard", "Standard"],
    "Ui_CompressStandardNote": ["Images at 150 dpi and photos as JPEG: sharp on screen and in print. Usually the right choice.", "画像を 150 dpi に、写真は JPEG に。画面でも印刷でもきれいです。通常はこれで十分です。", "图像降至 150 dpi，照片用 JPEG：屏幕和打印都清晰。一般选这个。", "影像降至 150 dpi，照片用 JPEG：螢幕和列印都清晰。一般選這個。", "이미지는 150 dpi, 사진은 JPEG 로. 화면과 인쇄 모두 선명합니다. 보통은 이것을 고르세요.", "Images à 150 dpi et photos en JPEG : net à l’écran comme à l’impression. Le choix habituel.", "Bilder mit 150 dpi, Fotos als JPEG: scharf am Bildschirm und im Druck. Meist die richtige Wahl."],
    "Ui_CompressStrong": ["Strong", "強め", "强力", "強力", "강하게", "Fort", "Stark"],
    "Ui_CompressStrongNote": ["Images at 100 dpi, stronger JPEG: the smallest file, for e-mail limits. Fine print in scans may soften.", "画像を 100 dpi に、JPEG も強めに。最も小さくなり、メールの容量制限向け。スキャンの細かい文字はやや甘くなることがあります。", "图像降至 100 dpi，JPEG 压缩更强：文件最小，适合邮件大小限制。扫描件中的小字可能稍模糊。", "影像降至 100 dpi，JPEG 壓縮更強：檔案最小，適合郵件大小限制。掃描件中的小字可能稍模糊。", "이미지 100 dpi, 더 강한 JPEG: 가장 작은 파일로 메일 용량 제한용입니다. 스캔의 작은 글자는 조금 흐려질 수 있습니다.", "Images à 100 dpi, JPEG plus fort : le fichier le plus léger, pour les limites d’e-mail. Les petits caractères des scans peuvent s’adoucir.", "Bilder mit 100 dpi, stärkeres JPEG: die kleinste Datei, für E-Mail-Grenzen. Kleine Schrift in Scans kann weicher werden."],
    "Ui_CompressRun": ["Compress and save copy…", "圧縮してコピーを保存…", "压缩并另存副本…", "壓縮並另存副本…", "압축하여 사본 저장…", "Compresser et enregistrer une copie…", "Verkleinern und Kopie speichern…"],
    "Ui_CompressRunNote": ["Form fields, links, bookmarks and text stay as they are. The original is not changed.", "記入欄・リンク・しおり・文字はそのまま残ります。元のファイルは変わりません。", "表单栏位、链接、书签和文字保持不变。原文件不会改变。", "表單欄位、連結、書籤和文字保持不變。原檔案不會改變。", "입력란·링크·책갈피·텍스트는 그대로 유지됩니다. 원본은 바뀌지 않습니다.", "Les champs, liens, signets et le texte restent tels quels. L’original n’est pas modifié.", "Formularfelder, Links, Lesezeichen und Text bleiben erhalten. Das Original bleibt unverändert."],
    "Ui_Compressing": ["Compressing…", "圧縮しています…", "正在压缩…", "正在壓縮…", "압축 중…", "Compression…", "Wird verkleinert …"],
    "Ui_Compressed": ["Saved {0}: {1} → {2} ({3}% smaller).", "{0} を保存しました: {1} → {2}（{3}% 削減）。", "已保存 {0}：{1} → {2}（减小 {3}%）。", "已儲存 {0}：{1} → {2}（減少 {3}%）。", "{0} 저장: {1} → {2} ({3}% 감소).", "{0} enregistré : {1} → {2} ({3} % de moins).", "{0} gespeichert: {1} → {2} ({3} % kleiner)."],
    "Ui_CompressDetails": ["{0} image(s) made smaller ({1} at a lower resolution), {2} duplicate(s) kept once, {3} stream(s) compressed.", "画像 {0} 件を縮小（うち解像度を下げたもの {1}）、重複 {2} 件を 1 つに、ストリーム {3} 件を圧縮。", "缩小图像 {0} 张（其中降低分辨率 {1} 张），合并重复 {2} 处，压缩数据流 {3} 个。", "縮小影像 {0} 張（其中降低解析度 {1} 張），合併重複 {2} 處，壓縮資料流 {3} 個。", "이미지 {0}개 축소(해상도 낮춤 {1}), 중복 {2}개 합침, 스트림 {3}개 압축.", "{0} image(s) allégée(s) (dont {1} en résolution réduite), {2} doublon(s) gardé(s) une fois, {3} flux compressé(s).", "{0} Bild(er) verkleinert ({1} mit geringerer Auflösung), {2} Duplikat(e) zusammengelegt, {3} Stream(s) komprimiert."],
    "Ui_CompressSigned": ["The file was digitally signed: the signatures do not hold for the compressed copy.", "このファイルにはデジタル署名がありました。圧縮したコピーでは署名は有効になりません。", "此文件带有数字签名：压缩后的副本中签名不再有效。", "此檔案帶有數位簽章：壓縮後的副本中簽章不再有效。", "이 파일에는 디지털 서명이 있었습니다. 압축한 사본에서는 서명이 유효하지 않습니다.", "Le fichier était signé numériquement : les signatures ne valent plus pour la copie compressée.", "Die Datei war digital signiert: für die verkleinerte Kopie gelten die Signaturen nicht."],
    "Err_CompressProtected": ["{0} is protected: remove its password first (Protect), compress it, then protect it again.", "{0} は保護されています。先に「保護」でパスワードを外してから圧縮し、もう一度保護してください。", "{0} 有保护：请先在“保护”中解除密码，压缩后再重新加密。", "{0} 有保護：請先在「保護」中解除密碼，壓縮後再重新加密。", "{0} 은(는) 보호되어 있습니다. 먼저 '보호'에서 암호를 해제하고 압축한 뒤 다시 보호하세요.", "{0} est protégé : retirez d’abord son mot de passe (Protéger), compressez-le, puis protégez-le de nouveau.", "{0} ist geschützt: zuerst das Passwort entfernen (Schützen), verkleinern, dann wieder schützen."],
    "Err_CompressChanged": ["The compressed copy looked different from the original (difference {0}), so it was not kept. Try a lighter level.", "圧縮したコピーの見た目が元と違った（差 {0}）ため、保存しませんでした。もっと軽いレベルをお試しください。", "压缩后的副本与原文件看起来不同（差异 {0}），因此没有保留。请试试较轻的级别。", "壓縮後的副本與原檔案看起來不同（差異 {0}），因此沒有保留。請試試較輕的級別。", "압축한 사본이 원본과 다르게 보여(차이 {0}) 저장하지 않았습니다. 더 가벼운 단계를 시도하세요.", "La copie compressée différait visiblement de l’original (écart {0}) ; elle n’a pas été conservée. Essayez un niveau plus léger.", "Die verkleinerte Kopie sah anders aus als das Original (Abweichung {0}) und wurde nicht behalten. Versuchen Sie eine leichtere Stufe."],
    "Err_CompressNoGain": ["{0} is already as small as Washi can make it at this level.", "{0} はこのレベルではこれ以上小さくできません。", "在此级别下，{0} 已无法再缩小。", "在此級別下，{0} 已無法再縮小。", "{0} 은(는) 이 단계에서 더 이상 줄일 수 없습니다.", "{0} est déjà aussi léger que Washi peut le rendre à ce niveau.", "{0} lässt sich auf dieser Stufe nicht weiter verkleinern."],
    "Sig_SignedBy": ["Digitally signed by {0}", "{0} によるデジタル署名", "由 {0} 数字签名", "由 {0} 數位簽署", "{0} 의 디지털 서명", "Signé numériquement par {0}", "Digital signiert von {0}"],
    "Sig_Date": ["Date: {0}", "日時: {0}", "日期：{0}", "日期：{0}", "날짜: {0}", "Date : {0}", "Datum: {0}"],
    "Sig_Reason": ["Reason: {0}", "理由: {0}", "原因：{0}", "原因：{0}", "사유: {0}", "Motif : {0}", "Grund: {0}"],
    "Sig_Location": ["Location: {0}", "場所: {0}", "地点：{0}", "地點：{0}", "장소: {0}", "Lieu : {0}", "Ort: {0}"],
    "Ui_IdFiles": ["Digital IDs (.p12, .pfx)", "デジタル ID（.p12, .pfx）", "数字标识（.p12, .pfx）", "數位識別碼（.p12, .pfx）", "디지털 ID (.p12, .pfx)", "Identités numériques (.p12, .pfx)", "Digitale IDs (.p12, .pfx)"],
    "Ui_SigInFile": ["Signatures in this file", "このファイルの署名", "此文件中的签名", "此檔案中的簽章", "이 파일의 서명", "Signatures de ce fichier", "Signaturen in dieser Datei"],
    "Ui_SigRow": ["{0} · {1}", "{0} · {1}", "{0} · {1}", "{0} · {1}", "{0} · {1}", "{0} · {1}", "{0} · {1}"],
    "Ui_SigStatus_valid": ["Valid: signer verified, and nothing changed since signing that the signature does not allow.", "有効: 署名者を確認済みで、署名後に署名が認めない変更はありません。", "有效：签名人已验证，签名后没有签名不允许的修改。", "有效：簽署人已驗證，簽署後沒有簽章不允許的修改。", "유효: 서명자가 확인되었고, 서명 후 서명이 허용하지 않는 변경은 없습니다.", "Valide : signataire vérifié, et rien n’a changé depuis la signature que celle-ci n’autorise.", "Gültig: Unterzeichner bestätigt, seit der Signatur keine Änderung, die sie nicht erlaubt."],
    "Ui_SigStatus_unverified": ["Unchanged since signing, but the signer's identity is not verified (no trusted certificate).", "署名後に変更はありませんが、署名者の身元は確認できません（信頼された証明書ではありません）。", "签名后未被修改，但无法验证签名人身份（证书不受信任）。", "簽署後未被修改，但無法驗證簽署人身分（憑證不受信任）。", "서명 후 변경은 없지만 서명자 신원은 확인되지 않았습니다 (신뢰할 수 있는 인증서가 아님).", "Inchangé depuis la signature, mais l’identité du signataire n’est pas vérifiée (certificat non approuvé).", "Seit der Signatur unverändert, aber die Identität des Unterzeichners ist nicht bestätigt (kein vertrauenswürdiges Zertifikat)."],
    "Ui_SigStatus_changed": ["Signature intact, but the document was changed after it in a way it does not allow.", "署名自体は有効ですが、署名後に署名が認めていない変更が文書に加えられています。", "签名完好，但文档在签名后被做了签名不允许的修改。", "簽章完好，但文件在簽署後被做了簽章不允許的修改。", "서명 자체는 유효하지만, 서명 후 서명이 허용하지 않는 방식으로 문서가 변경되었습니다.", "Signature intacte, mais le document a été modifié ensuite d’une façon qu’elle n’autorise pas.", "Signatur intakt, aber das Dokument wurde danach auf eine Weise geändert, die sie nicht erlaubt."],
    "Ui_SigStatus_invalid": ["Invalid: the document was altered, the signature is damaged, or the certificate was revoked.", "無効: 文書が改ざんされたか、署名が壊れているか、証明書が失効しています。", "无效：文档被篡改、签名已损坏，或证书已被吊销。", "無效：文件被竄改、簽章已損壞，或憑證已被撤銷。", "무효: 문서가 변조되었거나 서명이 손상되었거나 인증서가 폐기되었습니다.", "Non valide : document altéré, signature endommagée ou certificat révoqué.", "Ungültig: Dokument verändert, Signatur beschädigt oder Zertifikat widerrufen."],
    "Ui_SigTimestamped": ["time-stamped", "タイムスタンプ付き", "带时间戳", "附時間戳記", "타임스탬프 있음", "horodaté", "mit Zeitstempel"],
    "Ui_SigTitle": ["Sign with a digital ID", "デジタル ID で署名", "用数字标识签名", "以數位識別碼簽署", "디지털 ID 로 서명", "Signer avec une identité numérique", "Mit digitaler ID signieren"],
    "Ui_SigLead": ["A certificate-based signature that readers can check: it shows who signed and that nothing changed since. It is added after everything in the file, so earlier signatures stay valid.", "証明書による署名で、誰が署名したか、署名後に変更がないかを閲覧者が確認できます。ファイルの末尾に追加されるため、それまでの署名も有効なままです。", "基于证书的签名，阅读者可以核实谁签的、签名后是否被改动。签名追加在文件末尾，之前的签名仍然有效。", "以憑證為基礎的簽章，閱讀者可以核實誰簽署、簽署後是否被改動。簽章附加在檔案末尾，先前的簽章仍然有效。", "인증서 기반 서명으로, 누가 서명했고 이후 변경이 없는지 읽는 사람이 확인할 수 있습니다. 파일 끝에 추가되므로 이전 서명도 유효합니다.", "Une signature à certificat que les lecteurs peuvent vérifier : qui a signé, et que rien n’a changé depuis. Elle s’ajoute après le contenu du fichier : les signatures antérieures restent valides.", "Eine zertifikatbasierte Signatur, die Leser prüfen können: wer unterschrieben hat und dass seither nichts geändert wurde. Sie wird hinten angefügt, frühere Signaturen bleiben gültig."],
    "Ui_SigId": ["Digital ID", "デジタル ID", "数字标识", "數位識別碼", "디지털 ID", "Identité numérique", "Digitale ID"],
    "Ui_SigNoIds": ["No digital ID found on this computer. Open a .p12 / .pfx file, or make one here.", "このコンピューターにデジタル ID がありません。.p12 / .pfx ファイルを開くか、ここで作成してください。", "这台电脑上没有数字标识。请打开 .p12 / .pfx 文件，或在这里创建一个。", "這台電腦上沒有數位識別碼。請開啟 .p12 / .pfx 檔案，或在這裡建立一個。", "이 컴퓨터에 디지털 ID 가 없습니다. .p12 / .pfx 파일을 열거나 여기서 만드세요.", "Aucune identité numérique sur cet ordinateur. Ouvrez un fichier .p12 / .pfx, ou créez-en une ici.", "Keine digitale ID auf diesem Computer. Öffnen Sie eine .p12-/.pfx-Datei oder erstellen Sie hier eine."],
    "Ui_SigOpenId": ["Open ID file…", "ID ファイルを開く…", "打开标识文件…", "開啟識別碼檔案…", "ID 파일 열기…", "Ouvrir un fichier d’identité…", "ID-Datei öffnen…"],
    "Ui_SigCreateId": ["Make a digital ID", "デジタル ID を作成", "创建数字标识", "建立數位識別碼", "디지털 ID 만들기", "Créer une identité", "Digitale ID erstellen"],
    "Ui_SigNewName": ["Your name", "氏名", "姓名", "姓名", "이름", "Votre nom", "Ihr Name"],
    "Ui_SigNewEmail": ["E-mail (optional)", "メールアドレス（任意）", "电子邮件（可选）", "電子郵件（選填）", "이메일 (선택)", "E-mail (facultatif)", "E-Mail (optional)"],
    "Ui_SigNewPassword": ["Password for the ID file (6 characters or more)", "ID ファイルのパスワード（6 文字以上）", "标识文件的密码（至少 6 个字符）", "識別碼檔案的密碼（至少 6 個字元）", "ID 파일 암호 (6자 이상)", "Mot de passe du fichier (6 caractères ou plus)", "Passwort der ID-Datei (mindestens 6 Zeichen)"],
    "Ui_SigNewNote": ["A self-signed ID: it proves the document did not change, but readers see your identity as not verified until they choose to trust it. For legally recognised signatures, use an ID from a certification authority.", "自己署名の ID です。文書が変更されていないことは証明できますが、閲覧者が信頼するまで身元は「未確認」と表示されます。法的に認められた署名には、認証局が発行した ID を使ってください。", "这是自签名标识：能证明文档未被修改，但在阅读者选择信任之前，你的身份显示为“未验证”。如需具有法律效力的签名，请使用认证机构颁发的标识。", "這是自簽署識別碼：能證明文件未被修改，但在閱讀者選擇信任之前，你的身分顯示為「未驗證」。如需具有法律效力的簽章，請使用憑證機構頒發的識別碼。", "자체 서명 ID 입니다. 문서가 바뀌지 않았음은 증명하지만, 읽는 사람이 신뢰하기 전까지 신원은 '미확인'으로 표시됩니다. 법적 효력이 있는 서명에는 인증기관이 발급한 ID 를 사용하세요.", "Une identité auto-signée : elle prouve que le document n’a pas changé, mais les lecteurs voient votre identité comme non vérifiée tant qu’ils ne lui font pas confiance. Pour une signature reconnue juridiquement, utilisez une identité d’une autorité de certification.", "Eine selbstsignierte ID: sie belegt, dass das Dokument unverändert ist, aber Leser sehen Ihre Identität als unbestätigt, bis sie ihr vertrauen. Für rechtlich anerkannte Signaturen nutzen Sie eine ID einer Zertifizierungsstelle."],
    "Ui_SigCreateSave": ["Make and save…", "作成して保存…", "创建并保存…", "建立並儲存…", "만들어 저장…", "Créer et enregistrer…", "Erstellen und speichern…"],
    "Ui_IdCreated": ["Made the digital ID {0}. Keep the file and its password safe.", "デジタル ID {0} を作成しました。ファイルとパスワードは大切に保管してください。", "已创建数字标识 {0}。请妥善保管文件和密码。", "已建立數位識別碼 {0}。請妥善保管檔案和密碼。", "디지털 ID {0} 을(를) 만들었습니다. 파일과 암호를 안전하게 보관하세요.", "Identité {0} créée. Gardez le fichier et son mot de passe en lieu sûr.", "Digitale ID {0} erstellt. Bewahren Sie Datei und Passwort sicher auf."],
    "Ui_SigPlace": ["Where", "署名の場所", "位置", "位置", "위치", "Emplacement", "Wo"],
    "Ui_SigInvisible": ["Invisible (no mark on the page)", "非表示（ページには何も表示しない）", "不可见（页面上不显示）", "不可見（頁面上不顯示）", "보이지 않음 (페이지에 표시 없음)", "Invisible (rien sur la page)", "Unsichtbar (keine Markierung auf der Seite)"],
    "Ui_SigInField": ["Field “{0}” (page {1})", "記入欄「{0}」（{1} ページ）", "栏位“{0}”（第 {1} 页）", "欄位「{0}」（第 {1} 頁）", "입력란 “{0}” ({1}쪽)", "Champ « {0} » (page {1})", "Feld „{0}“ (Seite {1})"],
    "Ui_SigInBox": ["The box drawn on page {0}", "{0} ページに描いた枠", "第 {0} 页上画的框", "第 {0} 頁上畫的框", "{0}쪽에 그린 상자", "Le cadre tracé page {0}", "Der Rahmen auf Seite {0}"],
    "Ui_SigIncludeInk": ["Show my handwritten signature in it", "手書きのサインも表示する", "同时显示我的手写签名", "同時顯示我的手寫簽名", "손글씨 서명도 표시", "Y montrer ma signature manuscrite", "Meine handschriftliche Unterschrift zeigen"],
    "Ui_SigReason": ["Reason (optional)", "理由（任意）", "原因（可选）", "原因（選填）", "사유 (선택)", "Motif (facultatif)", "Grund (optional)"],
    "Ui_SigLocation": ["Location (optional)", "場所（任意）", "地点（可选）", "地點（選填）", "장소 (선택)", "Lieu (facultatif)", "Ort (optional)"],
    "Ui_SigTimestamp": ["Time-stamping service (optional)", "タイムスタンプサービス（任意）", "时间戳服务（可选）", "時間戳記服務（選填）", "타임스탬프 서비스 (선택)", "Service d’horodatage (facultatif)", "Zeitstempeldienst (optional)"],
    "Ui_SigTimestampNote": ["Leave empty to sign without one. If given, only the signature's hash is sent to it (RFC 3161), to prove when you signed.", "空欄なら使いません。指定すると、署名した時刻を証明するために署名のハッシュだけを送ります（RFC 3161）。", "留空则不使用。填写后只会发送签名的哈希值（RFC 3161），用于证明签名时间。", "留空則不使用。填寫後只會傳送簽章的雜湊值（RFC 3161），用於證明簽署時間。", "비워 두면 사용하지 않습니다. 입력하면 서명 시각 증명을 위해 서명의 해시만 보냅니다 (RFC 3161).", "Laissez vide pour signer sans. Sinon, seule l’empreinte de la signature lui est envoyée (RFC 3161), pour prouver quand vous avez signé.", "Leer lassen, um ohne zu signieren. Sonst wird nur der Hash der Signatur gesendet (RFC 3161), um den Zeitpunkt zu belegen."],
    "Ui_SigBadTimestamp": ["The time-stamping service is not a valid web address.", "タイムスタンプサービスのアドレスが正しくありません。", "时间戳服务的地址无效。", "時間戳記服務的位址無效。", "타임스탬프 서비스 주소가 올바르지 않습니다.", "L’adresse du service d’horodatage n’est pas valide.", "Die Adresse des Zeitstempeldienstes ist ungültig."],
    "Ui_SigSign": ["Sign and save copy…", "署名してコピーを保存…", "签名并另存副本…", "簽署並另存副本…", "서명하고 사본 저장…", "Signer et enregistrer une copie…", "Signieren und Kopie speichern…"],
    "Ui_SigSigned": ["Saved {0}, signed by {1}. The signature checks out.", "{0} を保存しました（{1} による署名）。署名を確認済みです。", "已保存 {0}（由 {1} 签名），签名已核实。", "已儲存 {0}（由 {1} 簽署），簽章已核實。", "{0} 저장 ({1} 서명). 서명 확인됨.", "{0} enregistré, signé par {1}. La signature a été vérifiée.", "{0} gespeichert, signiert von {1}. Die Signatur wurde geprüft."],
    "Err_SignDamaged": ["This file's structure could not be extended safely, so it was not signed.", "このファイルの構造に安全に追記できないため、署名しませんでした。", "无法安全地扩展此文件的结构，因此没有签名。", "無法安全地擴充此檔案的結構，因此沒有簽署。", "이 파일 구조에 안전하게 추가할 수 없어 서명하지 않았습니다.", "La structure de ce fichier ne peut pas être prolongée sans risque ; il n’a pas été signé.", "Die Struktur dieser Datei lässt sich nicht sicher erweitern; sie wurde nicht signiert."],
    "Err_SignProtected": ["Password-protected files cannot be signed by Washi: remove the password first.", "パスワード保護されたファイルには署名できません。先にパスワードを外してください。", "Washi 不能为有密码保护的文件签名，请先解除密码。", "Washi 不能為有密碼保護的檔案簽署，請先解除密碼。", "암호로 보호된 파일에는 서명할 수 없습니다. 먼저 암호를 해제하세요.", "Washi ne peut pas signer un fichier protégé : retirez d’abord le mot de passe.", "Washi kann geschützte Dateien nicht signieren: zuerst das Passwort entfernen."],
    "Err_SignNoField": ["There is no empty signature field “{0}”.", "空の署名欄「{0}」はありません。", "没有空的签名栏位“{0}”。", "沒有空的簽章欄位「{0}」。", "빈 서명란 “{0}” 이(가) 없습니다.", "Il n’y a pas de champ de signature vide « {0} ».", "Es gibt kein leeres Signaturfeld „{0}“."],
    "Err_SignFieldSigned": ["The field “{0}” is already signed.", "署名欄「{0}」はすでに署名済みです。", "栏位“{0}”已经签过名。", "欄位「{0}」已經簽署過。", "서명란 “{0}” 은(는) 이미 서명되었습니다.", "Le champ « {0} » est déjà signé.", "Das Feld „{0}“ ist bereits signiert."],
    "Err_SignCheck": ["The signed copy did not verify, so it was not kept.", "署名したコピーの確認に失敗したため、保存しませんでした。", "签名后的副本未通过核实，因此没有保留。", "簽署後的副本未通過核實，因此沒有保留。", "서명한 사본 확인에 실패해 저장하지 않았습니다.", "La copie signée ne s’est pas vérifiée ; elle n’a pas été conservée.", "Die signierte Kopie ließ sich nicht prüfen und wurde nicht behalten."],
    "Err_SignTimestamp": ["The time-stamping service ({0}) gave no valid time-stamp.", "タイムスタンプサービス（{0}）から有効なタイムスタンプを得られませんでした。", "时间戳服务（{0}）没有返回有效的时间戳。", "時間戳記服務（{0}）沒有傳回有效的時間戳記。", "타임스탬프 서비스({0})에서 유효한 타임스탬프를 받지 못했습니다.", "Le service d’horodatage ({0}) n’a pas fourni d’horodatage valide.", "Der Zeitstempeldienst ({0}) lieferte keinen gültigen Zeitstempel."],
    "Err_IdOpen": ["{0} could not be opened: the password is wrong or it is not a digital ID file.", "{0} を開けませんでした。パスワードが違うか、デジタル ID ファイルではありません。", "无法打开 {0}：密码错误，或者它不是数字标识文件。", "無法開啟 {0}：密碼錯誤，或者它不是數位識別碼檔案。", "{0} 을(를) 열 수 없습니다. 암호가 틀렸거나 디지털 ID 파일이 아닙니다.", "{0} n’a pas pu être ouvert : mot de passe erroné ou fichier qui n’est pas une identité numérique.", "{0} ließ sich nicht öffnen: falsches Passwort oder keine ID-Datei."],
    "Err_IdNoKey": ["{0} holds no private key, so it cannot sign.", "{0} には秘密鍵がないため、署名できません。", "{0} 不含私钥，因此不能签名。", "{0} 不含私密金鑰，因此不能簽署。", "{0} 에 개인 키가 없어 서명할 수 없습니다.", "{0} ne contient pas de clé privée : il ne peut pas signer.", "{0} enthält keinen privaten Schlüssel und kann nicht signieren."],
    "Err_IdName": ["A digital ID needs a name.", "デジタル ID には氏名が必要です。", "数字标识需要一个姓名。", "數位識別碼需要一個姓名。", "디지털 ID 에는 이름이 필요합니다.", "Une identité numérique a besoin d’un nom.", "Eine digitale ID braucht einen Namen."],
    "Err_IdPassword": ["The password must be at least 6 characters.", "パスワードは 6 文字以上にしてください。", "密码至少需要 6 个字符。", "密碼至少需要 6 個字元。", "암호는 6자 이상이어야 합니다.", "Le mot de passe doit compter au moins 6 caractères.", "Das Passwort muss mindestens 6 Zeichen lang sein."],
    "Pdf_SigNone": ["No signatures.", "署名はありません。", "没有签名。", "沒有簽章。", "서명이 없습니다.", "Aucune signature.", "Keine Signaturen."],
    "Pdf_SigLine": ["{0}: {1} — {2}", "{0}: {1} — {2}", "{0}：{1} — {2}", "{0}：{1} — {2}", "{0}: {1} — {2}", "{0} : {1} — {2}", "{0}: {1} — {2}"],
    "Pdf_AskIdPassword": ["Password of the digital ID: ", "デジタル ID のパスワード: ", "数字标识的密码：", "數位識別碼的密碼：", "디지털 ID 암호: ", "Mot de passe de l’identité : ", "Passwort der digitalen ID: "],
    "Ui_ToolEditText": ["Edit text", "文字の編集", "编辑文字", "編輯文字", "글자 편집", "Modifier le texte", "Text bearbeiten"],
    "Ui_EditTextTitle": ["Edit text", "文字の編集", "编辑文字", "編輯文字", "글자 편집", "Modifier le texte", "Text bearbeiten"],
    "Ui_EditTextLead": ["Change the words printed on the page: click a line and type, or find and replace. The new text takes the old one's place, size and colour, in the document's own font when it has the characters.", "ページに印刷された文字を書き換えます。行をクリックして入力するか、検索して置換します。新しい文字は元の位置・大きさ・色で、文書のフォントに文字があればそのフォントで描かれます。", "修改页面上印刷的文字：点击一行并输入，或查找并替换。新文字会放在原来的位置，大小和颜色不变；文档字体有这些字时就用文档字体。", "修改頁面上印刷的文字：點一行並輸入，或搜尋並取代。新文字會放在原來的位置，大小和顏色不變；文件字型有這些字時就用文件字型。", "페이지에 인쇄된 글자를 고칩니다. 줄을 클릭해 입력하거나 찾아서 바꿉니다. 새 글자는 원래 자리에 같은 크기와 색으로, 문서 글꼴에 글자가 있으면 그 글꼴로 그립니다.", "Modifiez les mots imprimés sur la page : cliquez sur une ligne et tapez, ou cherchez et remplacez. Le nouveau texte prend la place, la taille et la couleur de l’ancien, dans la police du document quand elle a les caractères.", "Ändern Sie die auf der Seite gedruckten Wörter: eine Zeile anklicken und tippen, oder suchen und ersetzen. Der neue Text steht an der Stelle des alten, in gleicher Größe und Farbe, in der Schrift des Dokuments, wenn sie die Zeichen hat."],
    "Ui_EditTextLine": ["Line", "行", "行", "行", "줄", "Ligne", "Zeile"],
    "Ui_EditTextClick": ["Click a line of text on the page.", "ページの文字の行をクリックしてください。", "请点击页面上的一行文字。", "請點頁面上的一行文字。", "페이지의 글자 줄을 클릭하세요.", "Cliquez sur une ligne de texte de la page.", "Klicken Sie auf eine Textzeile der Seite."],
    "Ui_EditTextKeep": ["Keep", "確定", "确定", "確定", "확정", "Garder", "Übernehmen"],
    "Ui_EditTextKeys": ["Enter keeps the change, Esc leaves the line as it was.", "Enter で確定、Esc で元のまま。", "Enter 确定，Esc 保持原样。", "Enter 確定，Esc 保持原樣。", "Enter로 확정, Esc로 그대로 둡니다.", "Entrée garde la modification, Échap laisse la ligne telle quelle.", "Eingabe übernimmt die Änderung, Esc lässt die Zeile, wie sie war."],
    "Ui_EditTextFindTitle": ["Find and replace", "検索と置換", "查找和替换", "搜尋和取代", "찾아 바꾸기", "Rechercher et remplacer", "Suchen und ersetzen"],
    "Ui_EditTextFindHint": ["Text to find", "検索する語句", "要查找的文字", "要搜尋的文字", "찾을 텍스트", "Texte à chercher", "Zu suchender Text"],
    "Ui_EditTextReplaceHint": ["Replace with", "置換後の語句", "替换为", "取代為", "바꿀 텍스트", "Remplacer par", "Ersetzen durch"],
    "Ui_EditTextReplaceAll": ["Replace all", "すべて置換", "全部替换", "全部取代", "모두 바꾸기", "Tout remplacer", "Alle ersetzen"],
    "Ui_EditTextReplaced": ["{0} change(s) added: check them on the page, then save.", "{0} 件の変更を追加しました。ページで確認してから保存してください。", "已添加 {0} 处修改：请在页面上确认后保存。", "已新增 {0} 處修改：請在頁面上確認後儲存。", "변경 {0}개를 추가했습니다. 페이지에서 확인한 뒤 저장하세요.", "{0} modification(s) ajoutée(s) : vérifiez-les sur la page, puis enregistrez.", "{0} Änderung(en) hinzugefügt: auf der Seite prüfen, dann speichern."],
    "Ui_EditTextNotFound": ["“{0}” is not in the document.", "「{0}」は文書にありません。", "文档中没有“{0}”。", "文件中沒有「{0}」。", "문서에 “{0}”이(가) 없습니다.", "« {0} » n’est pas dans le document.", "„{0}“ steht nicht im Dokument."],
    "Ui_EditTextChanges": ["Changes", "変更", "修改", "修改", "변경", "Modifications", "Änderungen"],
    "Ui_EditTextNoChanges": ["No changes yet.", "まだ変更はありません。", "还没有修改。", "還沒有修改。", "아직 변경이 없습니다.", "Aucune modification pour l’instant.", "Noch keine Änderungen."],
    "Ui_EditTextOnPage": ["Page {0}", "{0} ページ", "第 {0} 页", "第 {0} 頁", "{0}페이지", "Page {0}", "Seite {0}"],
    "Ui_EditTextChange": ["{0} → {1}", "{0} → {1}", "{0} → {1}", "{0} → {1}", "{0} → {1}", "{0} → {1}", "{0} → {1}"],
    "Ui_EditTextNothing": ["(removed)", "（削除）", "（删除）", "（刪除）", "(삭제)", "(supprimé)", "(entfernt)"],
    "Ui_EditTextSave": ["Save {0} change(s) as copy…", "{0} 件の変更をコピーに保存…", "将 {0} 处修改另存为副本…", "將 {0} 處修改另存為副本…", "변경 {0}개를 사본으로 저장…", "Enregistrer {0} modification(s) dans une copie…", "{0} Änderung(en) als Kopie speichern…"],
    "Ui_EditTextSaveNote": ["The copy is checked before it is kept: nothing else on the page changes and the new text reads back. The original is not changed.", "保存前にコピーを確認します（ページのほかの部分が変わらず、新しい文字が読み取れること）。元のファイルは変わりません。", "保存前会检查副本：页面其他部分不变，新文字可以读出。原文件不会改变。", "儲存前會檢查副本：頁面其他部分不變，新文字可以讀出。原檔案不會改變。", "저장하기 전에 사본을 확인합니다 (페이지의 다른 부분은 그대로이고 새 글자가 읽힘). 원본은 바뀌지 않습니다.", "La copie est vérifiée avant d’être gardée : rien d’autre ne change sur la page et le nouveau texte se relit. L’original n’est pas modifié.", "Die Kopie wird vor dem Behalten geprüft: sonst ändert sich nichts auf der Seite, und der neue Text lässt sich lesen. Das Original bleibt unverändert."],
    "Ui_TextEdited": ["Saved {0}: {1} change(s), in the document's own fonts. Checked: nothing else moved.", "{0} を保存しました：{1} 件の変更（文書のフォントで描画）。確認済み：ほかの部分は変わっていません。", "已保存 {0}：{1} 处修改，使用文档自己的字体。已检查：其他部分没有变化。", "已儲存 {0}：{1} 處修改，使用文件自己的字型。已檢查：其他部分沒有變化。", "{0} 저장: 변경 {1}개, 문서 글꼴로 그림. 확인: 다른 부분은 바뀌지 않았습니다.", "{0} enregistré : {1} modification(s), dans les polices du document. Vérifié : rien d’autre n’a bougé.", "{0} gespeichert: {1} Änderung(en), in den Schriften des Dokuments. Geprüft: sonst hat sich nichts verschoben."],
    "Ui_TextEditedSimilar": ["Saved {0}: {1} change(s); {2} drawn in a similar font because the document's font lacks some characters. Checked: nothing else moved.", "{0} を保存しました：{1} 件の変更。文書のフォントに文字がない {2} 件は似たフォントで描きました。確認済み：ほかの部分は変わっていません。", "已保存 {0}：{1} 处修改；其中 {2} 处因文档字体缺字而使用相近字体。已检查：其他部分没有变化。", "已儲存 {0}：{1} 處修改；其中 {2} 處因文件字型缺字而使用相近字型。已檢查：其他部分沒有變化。", "{0} 저장: 변경 {1}개. 문서 글꼴에 없는 글자가 있어 {2}개는 비슷한 글꼴로 그렸습니다. 확인: 다른 부분은 바뀌지 않았습니다.", "{0} enregistré : {1} modification(s) ; {2} dessinée(s) dans une police proche, car celle du document n’a pas tous les caractères. Vérifié : rien d’autre n’a bougé.", "{0} gespeichert: {1} Änderung(en); {2} in einer ähnlichen Schrift, weil der Schrift des Dokuments Zeichen fehlen. Geprüft: sonst hat sich nichts verschoben."],
    "Pdf_TextEditKept": ["page {0}: “{1}” → “{2}” ({3})", "{0} ページ：「{1}」→「{2}」（{3}）", "第 {0} 页：“{1}” → “{2}”（{3}）", "第 {0} 頁：「{1}」→「{2}」（{3}）", "{0}페이지: “{1}” → “{2}” ({3})", "page {0} : « {1} » → « {2} » ({3})", "Seite {0}: „{1}“ → „{2}“ ({3})"],
    "Pdf_TextEditSimilar": ["page {0}: “{1}” → “{2}” (in a similar font, {3})", "{0} ページ：「{1}」→「{2}」（似たフォント {3}）", "第 {0} 页：“{1}” → “{2}”（相近字体 {3}）", "第 {0} 頁：「{1}」→「{2}」（相近字型 {3}）", "{0}페이지: “{1}” → “{2}” (비슷한 글꼴 {3})", "page {0} : « {1} » → « {2} » (police proche, {3})", "Seite {0}: „{1}“ → „{2}“ (ähnliche Schrift, {3})"],
    "Pdf_TextEdited": ["Saved {0}: {1} change(s). Checked: nothing else changed, the new text reads back.", "{0} を保存しました：{1} 件の変更。確認済み：ほかは変わらず、新しい文字が読み取れます。", "已保存 {0}：{1} 处修改。已检查：其他部分不变，新文字可以读出。", "已儲存 {0}：{1} 處修改。已檢查：其他部分不變，新文字可以讀出。", "{0} 저장: 변경 {1}개. 확인: 다른 부분은 그대로이고 새 글자가 읽힙니다.", "{0} enregistré : {1} modification(s). Vérifié : rien d’autre n’a changé, le nouveau texte se relit.", "{0} gespeichert: {1} Änderung(en). Geprüft: sonst nichts verändert, der neue Text lässt sich lesen."],
    "Err_EditNothing": ["No text to change.", "変更する文字がありません。", "没有要修改的文字。", "沒有要修改的文字。", "바꿀 글자가 없습니다.", "Aucun texte à modifier.", "Kein Text zu ändern."],
    "Err_EditNoText": ["There is no text in that place on page {0}.", "{0} ページのその場所には文字がありません。", "第 {0} 页的那个位置没有文字。", "第 {0} 頁的那個位置沒有文字。", "{0}페이지의 그 자리에는 글자가 없습니다.", "Il n’y a pas de texte à cet endroit de la page {0}.", "An dieser Stelle von Seite {0} steht kein Text."],
    "Err_EditNotHorizontal": ["Only text that runs left to right can be edited.", "編集できるのは左から右へ横書きの文字だけです。", "只能编辑从左到右横排的文字。", "只能編輯從左到右橫排的文字。", "왼쪽에서 오른쪽으로 쓴 가로 글자만 고칠 수 있습니다.", "Seul le texte écrit de gauche à droite peut être modifié.", "Nur Text, der von links nach rechts läuft, kann bearbeitet werden."],
    "Err_EditOneLine": ["That place holds more than one line: edit one line at a time.", "その場所には複数の行があります。1 行ずつ編集してください。", "那个位置有不止一行：请一次编辑一行。", "那個位置有不只一行：請一次編輯一行。", "그 자리에 여러 줄이 있습니다. 한 줄씩 고치세요.", "Cet endroit contient plus d’une ligne : modifiez une ligne à la fois.", "An dieser Stelle steht mehr als eine Zeile: bearbeiten Sie eine Zeile auf einmal."],
    "Err_EditTooLong": ["“{0}” is longer than “{1}” and would run into the text after it on the line: change the whole line instead.", "「{0}」は「{1}」より長く、同じ行の後ろの文字に重なります。行全体を書き換えてください。", "“{0}”比“{1}”长，会压到同一行后面的文字：请改为修改整行。", "「{0}」比「{1}」長，會壓到同一行後面的文字：請改為修改整行。", "“{0}”이(가) “{1}”보다 길어 같은 줄 뒤의 글자와 겹칩니다. 줄 전체를 고치세요.", "« {0} » est plus long que « {1} » et empiéterait sur le texte qui suit sur la ligne : modifiez plutôt toute la ligne.", "„{0}“ ist länger als „{1}“ und würde in den folgenden Text der Zeile laufen: ändern Sie stattdessen die ganze Zeile."],
    "Err_EditChanged": ["The edited copy changed {0} pixels outside the edited text, so it was not kept.", "編集したコピーが編集箇所の外で {0} 画素変わったため、保存しませんでした。", "编辑后的副本在修改处以外变化了 {0} 个像素，因此没有保留。", "編輯後的副本在修改處以外變化了 {0} 個像素，因此沒有保留。", "편집한 사본이 고친 곳 밖에서 {0} 픽셀 바뀌어 저장하지 않았습니다.", "La copie modifiée changeait {0} pixels hors du texte modifié ; elle n’a pas été conservée.", "Die bearbeitete Kopie änderte {0} Pixel außerhalb des bearbeiteten Texts; sie wurde nicht behalten."],
    "Err_EditCheck": ["The new text did not read back as typed (read “{0}”), so the copy was not kept.", "新しい文字が入力どおりに読み取れなかった（「{0}」）ため、保存しませんでした。", "新文字没有按输入读出（读到“{0}”），因此没有保留副本。", "新文字沒有按輸入讀出（讀到「{0}」），因此沒有保留副本。", "새 글자가 입력한 대로 읽히지 않아 (“{0}”) 사본을 저장하지 않았습니다.", "Le nouveau texte ne se relisait pas tel que saisi (lu « {0} ») ; la copie n’a pas été conservée.", "Der neue Text ließ sich nicht wie eingegeben lesen (gelesen: „{0}“); die Kopie wurde nicht behalten."],
    "Err_EditNotFound": ["“{0}” is not in the document.", "「{0}」は文書にありません。", "文档中没有“{0}”。", "文件中沒有「{0}」。", "문서에 “{0}”이(가) 없습니다.", "« {0} » n’est pas dans le document.", "„{0}“ steht nicht im Dokument."],
    "Ui_ToolStamp": ["Headers", "ヘッダー", "页眉水印", "頁首浮水印", "머리글", "En-têtes", "Kopfzeilen"],
    "Ui_StampTitle": ["Headers, footers and watermark", "ヘッダー・フッター・透かし", "页眉、页脚和水印", "頁首、頁尾和浮水印", "머리글, 바닥글, 워터마크", "En-têtes, pieds de page et filigrane", "Kopf-, Fußzeilen und Wasserzeichen"],
    "Ui_StampLead": ["Add text at the top and bottom of the pages (page numbers, the file name, a date, Bates numbers) and a see-through watermark across them. The page itself is not changed.", "ページの上下に文字（ページ番号・ファイル名・日付・ベイツ番号）を、ページの上に透ける透かしを入れます。ページそのものは変わりません。", "在页面上下加入文字（页码、文件名、日期、贝茨编号），并在页面上加入半透明水印。页面本身不会改变。", "在頁面上下加入文字（頁碼、檔名、日期、貝茨編號），並在頁面上加入半透明浮水印。頁面本身不會改變。", "페이지 위아래에 글자(쪽 번호, 파일 이름, 날짜, 베이츠 번호)를, 페이지 위에 비치는 워터마크를 넣습니다. 페이지 자체는 바뀌지 않습니다.", "Ajoutez du texte en haut et en bas des pages (numéros de page, nom du fichier, date, numéros Bates) et un filigrane transparent. La page elle-même n’est pas modifiée.", "Fügen Sie oben und unten auf den Seiten Text hinzu (Seitenzahlen, Dateiname, Datum, Bates-Nummern) und ein durchscheinendes Wasserzeichen. Die Seite selbst bleibt unverändert."],
    "Ui_StampHeader": ["Header", "ヘッダー", "页眉", "頁首", "머리글", "En-tête", "Kopfzeile"],
    "Ui_StampFooter": ["Footer", "フッター", "页脚", "頁尾", "바닥글", "Pied de page", "Fußzeile"],
    "Ui_StampLeft": ["Left", "左", "左", "左", "왼쪽", "Gauche", "Links"],
    "Ui_StampCenter": ["Centre", "中央", "中", "中", "가운데", "Centre", "Mitte"],
    "Ui_StampRight": ["Right", "右", "右", "右", "오른쪽", "Droite", "Rechts"],
    "Ui_StampTokens": ["{page} page number, {pages} page count, {date} today, {file} file name, {bates} Bates number", "{page} ページ番号、{pages} 総ページ数、{date} 今日の日付、{file} ファイル名、{bates} ベイツ番号", "{page} 页码，{pages} 总页数，{date} 今天日期，{file} 文件名，{bates} 贝茨编号", "{page} 頁碼，{pages} 總頁數，{date} 今天日期，{file} 檔名，{bates} 貝茨編號", "{page} 쪽 번호, {pages} 전체 쪽수, {date} 오늘 날짜, {file} 파일 이름, {bates} 베이츠 번호", "{page} numéro de page, {pages} nombre de pages, {date} date du jour, {file} nom du fichier, {bates} numéro Bates", "{page} Seitenzahl, {pages} Seitenanzahl, {date} heutiges Datum, {file} Dateiname, {bates} Bates-Nummer"],
    "Ui_StampPageNumbers": ["Page numbers", "ページ番号", "页码", "頁碼", "쪽 번호", "Numéros de page", "Seitenzahlen"],
    "Ui_StampBates": ["Bates numbers", "ベイツ番号", "贝茨编号", "貝茨編號", "베이츠 번호", "Numéros Bates", "Bates-Nummern"],
    "Ui_StampSize": ["Size", "大きさ", "字号", "字級", "크기", "Taille", "Größe"],
    "Ui_StampPages": ["Pages", "ページ", "页面", "頁面", "페이지", "Pages", "Seiten"],
    "Ui_StampAllPages": ["All pages (or e.g. 2-5)", "すべて（例：2-5）", "全部（例如 2-5）", "全部（例如 2-5）", "전체 (예: 2-5)", "Toutes (ou p. ex. 2-5)", "Alle (oder z. B. 2-5)"],
    "Ui_StampBatesTitle": ["Bates numbers", "ベイツ番号", "贝茨编号", "貝茨編號", "베이츠 번호", "Numéros Bates", "Bates-Nummern"],
    "Ui_StampBatesPrefix": ["Prefix (e.g. ACME-)", "接頭辞（例：ACME-）", "前缀（例如 ACME-）", "前綴（例如 ACME-）", "접두사 (예: ACME-)", "Préfixe (p. ex. ACME-)", "Präfix (z. B. ACME-)"],
    "Ui_StampBatesStart": ["Start", "開始", "起始", "起始", "시작", "Début", "Start"],
    "Ui_StampBatesDigits": ["Digits", "桁数", "位数", "位數", "자릿수", "Chiffres", "Stellen"],
    "Ui_StampWatermark": ["Watermark", "透かし", "水印", "浮水印", "워터마크", "Filigrane", "Wasserzeichen"],
    "Ui_StampWatermarkHint": ["e.g. CONFIDENTIAL, 社外秘", "例：社外秘、CONFIDENTIAL", "例如：机密、CONFIDENTIAL", "例如：機密、CONFIDENTIAL", "예: 대외비, CONFIDENTIAL", "p. ex. CONFIDENTIEL", "z. B. VERTRAULICH"],
    "Ui_StampOpacity": ["Opacity", "不透明度", "不透明度", "不透明度", "불투명도", "Opacité", "Deckkraft"],
    "Ui_StampAngle": ["Angle", "角度", "角度", "角度", "각도", "Angle", "Winkel"],
    "Ui_StampGrey": ["Grey", "グレー", "灰色", "灰色", "회색", "Gris", "Grau"],
    "Ui_StampRed": ["Red", "赤", "红色", "紅色", "빨간색", "Rouge", "Rot"],
    "Ui_StampBlue": ["Blue", "青", "蓝色", "藍色", "파란색", "Bleu", "Blau"],
    "Ui_StampBehind": ["Behind the page's content", "ページの内容の後ろに置く", "放在页面内容后面", "放在頁面內容後面", "페이지 내용 뒤에 두기", "Derrière le contenu de la page", "Hinter den Seiteninhalt"],
    "Ui_StampSave": ["Add and save copy…", "追加してコピーを保存…", "添加并另存副本…", "加入並另存副本…", "추가하고 사본 저장…", "Ajouter et enregistrer une copie…", "Hinzufügen und Kopie speichern…"],
    "Ui_StampSaveNote": ["The copy is checked before it is kept: nothing else on the pages changes and every text reads back. The original is not changed.", "保存前にコピーを確認します（ページのほかの部分が変わらず、すべての文字が読み取れること）。元のファイルは変わりません。", "保存前会检查副本：页面其他部分不变，所有文字都能读出。原文件不会改变。", "儲存前會檢查副本：頁面其他部分不變，所有文字都能讀出。原檔案不會改變。", "저장하기 전에 사본을 확인합니다 (페이지의 다른 부분은 그대로이고 모든 글자가 읽힘). 원본은 바뀌지 않습니다.", "La copie est vérifiée avant d’être gardée : rien d’autre ne change sur les pages et chaque texte se relit. L’original n’est pas modifié.", "Die Kopie wird vor dem Behalten geprüft: sonst ändert sich nichts auf den Seiten, und jeder Text lässt sich lesen. Das Original bleibt unverändert."],
    "Ui_Stamped": ["Saved {0}: {1} page(s) marked. Checked: nothing else moved.", "{0} を保存しました：{1} ページに追加。確認済み：ほかの部分は変わっていません。", "已保存 {0}：已添加到 {1} 页。已检查：其他部分没有变化。", "已儲存 {0}：已加入 {1} 頁。已檢查：其他部分沒有變化。", "{0} 저장: {1}쪽에 추가. 확인: 다른 부분은 바뀌지 않았습니다.", "{0} enregistré : {1} page(s) marquée(s). Vérifié : rien d’autre n’a bougé.", "{0} gespeichert: {1} Seite(n) versehen. Geprüft: sonst hat sich nichts verschoben."],
    "Ui_StampedBates": ["Saved {0}: {1} page(s) marked, Bates {2} to {3}. Checked: nothing else moved.", "{0} を保存しました：{1} ページに追加、ベイツ番号 {2}〜{3}。確認済み：ほかの部分は変わっていません。", "已保存 {0}：已添加到 {1} 页，贝茨编号 {2} 至 {3}。已检查：其他部分没有变化。", "已儲存 {0}：已加入 {1} 頁，貝茨編號 {2} 至 {3}。已檢查：其他部分沒有變化。", "{0} 저장: {1}쪽에 추가, 베이츠 {2}~{3}. 확인: 다른 부분은 바뀌지 않았습니다.", "{0} enregistré : {1} page(s) marquée(s), Bates {2} à {3}. Vérifié : rien d’autre n’a bougé.", "{0} gespeichert: {1} Seite(n) versehen, Bates {2} bis {3}. Geprüft: sonst hat sich nichts verschoben."],
    "Pdf_Stamped": ["Saved {0}: {1} page(s) marked. Checked: nothing else changed, every text reads back.", "{0} を保存しました：{1} ページに追加。確認済み：ほかは変わらず、すべての文字が読み取れます。", "已保存 {0}：已添加到 {1} 页。已检查：其他部分不变，所有文字都能读出。", "已儲存 {0}：已加入 {1} 頁。已檢查：其他部分不變，所有文字都能讀出。", "{0} 저장: {1}쪽에 추가. 확인: 다른 부분은 그대로이고 모든 글자가 읽힙니다.", "{0} enregistré : {1} page(s) marquée(s). Vérifié : rien d’autre n’a changé, chaque texte se relit.", "{0} gespeichert: {1} Seite(n) versehen. Geprüft: sonst nichts verändert, jeder Text lässt sich lesen."],
    "Pdf_StampedBates": ["Saved {0}: {1} page(s) marked, Bates {2} to {3}. Checked: nothing else changed, every text reads back.", "{0} を保存しました：{1} ページに追加、ベイツ番号 {2}〜{3}。確認済み：ほかは変わらず、すべての文字が読み取れます。", "已保存 {0}：已添加到 {1} 页，贝茨编号 {2} 至 {3}。已检查：其他部分不变，所有文字都能读出。", "已儲存 {0}：已加入 {1} 頁，貝茨編號 {2} 至 {3}。已檢查：其他部分不變，所有文字都能讀出。", "{0} 저장: {1}쪽에 추가, 베이츠 {2}~{3}. 확인: 다른 부분은 그대로이고 모든 글자가 읽힙니다.", "{0} enregistré : {1} page(s) marquée(s), Bates {2} à {3}. Vérifié : rien d’autre n’a changé, chaque texte se relit.", "{0} gespeichert: {1} Seite(n) versehen, Bates {2} bis {3}. Geprüft: sonst nichts verändert, jeder Text lässt sich lesen."],
    "Err_StampNothing": ["Nothing to add: type a header, a footer or a watermark.", "追加するものがありません。ヘッダー・フッター・透かしのどれかを入力してください。", "没有要添加的内容：请输入页眉、页脚或水印。", "沒有要加入的內容：請輸入頁首、頁尾或浮水印。", "추가할 내용이 없습니다. 머리글, 바닥글 또는 워터마크를 입력하세요.", "Rien à ajouter : saisissez un en-tête, un pied de page ou un filigrane.", "Nichts hinzuzufügen: geben Sie eine Kopf-, Fußzeile oder ein Wasserzeichen ein."],
    "Err_StampSize": ["The text size must be 4 to 72 points.", "文字の大きさは 4〜72 ポイントにしてください。", "字号必须在 4 到 72 点之间。", "字級必須在 4 到 72 點之間。", "글자 크기는 4~72포인트여야 합니다.", "La taille du texte doit être de 4 à 72 points.", "Die Textgröße muss 4 bis 72 Punkt betragen."],
    "Err_StampWatermark": ["The watermark's opacity must be above 0 and at most 100%, its size 0 to 400 points.", "透かしの不透明度は 0 より大きく 100% 以下、大きさは 0〜400 ポイントにしてください。", "水印的不透明度必须大于 0 且不超过 100%，字号在 0 到 400 点之间。", "浮水印的不透明度必須大於 0 且不超過 100%，字級在 0 到 400 點之間。", "워터마크의 불투명도는 0보다 크고 100% 이하, 크기는 0~400포인트여야 합니다.", "L’opacité du filigrane doit être supérieure à 0 et au plus 100 %, sa taille de 0 à 400 points.", "Die Deckkraft des Wasserzeichens muss über 0 und höchstens 100 % liegen, seine Größe 0 bis 400 Punkt."],
    "Err_StampChanged": ["Page {0} changed outside the header, footer and watermark, so the copy was not kept.", "{0} ページがヘッダー・フッター・透かし以外の場所で変わったため、保存しませんでした。", "第 {0} 页在页眉、页脚和水印以外发生了变化，因此没有保留副本。", "第 {0} 頁在頁首、頁尾和浮水印以外發生了變化，因此沒有保留副本。", "{0}쪽이 머리글·바닥글·워터마크 밖에서 바뀌어 사본을 저장하지 않았습니다.", "La page {0} a changé hors de l’en-tête, du pied de page et du filigrane ; la copie n’a pas été conservée.", "Seite {0} hat sich außerhalb von Kopf-, Fußzeile und Wasserzeichen verändert; die Kopie wurde nicht behalten."],
    "Err_StampCheck": ["“{0}” did not read back on page {1}, so the copy was not kept.", "{1} ページで「{0}」が読み取れなかったため、保存しませんでした。", "第 {1} 页读不出“{0}”，因此没有保留副本。", "第 {1} 頁讀不出「{0}」，因此沒有保留副本。", "{1}쪽에서 “{0}”이(가) 읽히지 않아 사본을 저장하지 않았습니다.", "« {0} » ne se relisait pas sur la page {1} ; la copie n’a pas été conservée.", "„{0}“ ließ sich auf Seite {1} nicht lesen; die Kopie wurde nicht behalten."],
    "Ui_ImageFile": ["Image · becomes one page", "画像 · 1 ページになります", "图片 · 成为一页", "圖片 · 成為一頁", "이미지 · 한 페이지가 됩니다", "Image · devient une page", "Bild · wird eine Seite"],
    "Ui_ImageFiles": ["Images", "画像", "图片", "圖片", "이미지", "Images", "Bilder"],
    "Ui_PdfOrImageFiles": ["PDFs and images", "PDF と画像", "PDF 和图片", "PDF 和圖片", "PDF 와 이미지", "PDF et images", "PDFs und Bilder"],
    "Ui_ImagesPage": ["Page for each image", "画像を置くページ", "图片所在的页面", "圖片所在的頁面", "이미지를 놓을 페이지", "Page de chaque image", "Seite für jedes Bild"],
    "Ui_ImagesA4": ["A4, turned to suit the image", "A4（画像に合わせて縦横）", "A4（随图片横竖）", "A4（隨圖片橫直）", "A4 (이미지에 맞춰 가로세로)", "A4, orientée selon l’image", "A4, nach dem Bild gedreht"],
    "Ui_ImagesLetter": ["Letter, turned to suit the image", "レター（画像に合わせて縦横）", "Letter（随图片横竖）", "Letter（隨圖片橫直）", "레터 (이미지에 맞춰 가로세로)", "Lettre US, orientée selon l’image", "Letter, nach dem Bild gedreht"],
    "Ui_ImagesOwnSize": ["The image's own size", "画像と同じ大きさ", "与图片相同大小", "與圖片相同大小", "이미지와 같은 크기", "La taille de l’image", "Die Größe des Bildes"],
    "Ui_ImagesCombined": ["Made {1} from {0} image(s). Checked: each page looks like its image.", "{0} 枚の画像から {1} を作りました。確認済み：各ページが元の画像と同じに見えます。", "已用 {0} 张图片生成 {1}。已检查：每页都与原图一致。", "已用 {0} 張圖片產生 {1}。已檢查：每頁都與原圖一致。", "이미지 {0}장으로 {1}을(를) 만들었습니다. 확인: 각 페이지가 원래 이미지와 같습니다.", "{1} créé à partir de {0} image(s). Vérifié : chaque page ressemble à son image.", "{1} aus {0} Bild(ern) erstellt. Geprüft: jede Seite sieht aus wie ihr Bild."],
    "Pdf_FromImages": ["Saved {0}: {1} page(s), {2} JPEG(s) kept as they were. Checked: each page looks like its image.", "{0} を保存しました：{1} ページ、JPEG {2} 枚はそのまま。確認済み：各ページが元の画像と同じに見えます。", "已保存 {0}：{1} 页，{2} 张 JPEG 原样保留。已检查：每页都与原图一致。", "已儲存 {0}：{1} 頁，{2} 張 JPEG 原樣保留。已檢查：每頁都與原圖一致。", "{0} 저장: {1}쪽, JPEG {2}장은 그대로. 확인: 각 페이지가 원래 이미지와 같습니다.", "{0} enregistré : {1} page(s), {2} JPEG gardé(s) tel(s) quel(s). Vérifié : chaque page ressemble à son image.", "{0} gespeichert: {1} Seite(n), {2} JPEG(s) unverändert übernommen. Geprüft: jede Seite sieht aus wie ihr Bild."],
    "Err_ImagesNone": ["No images to put in a PDF.", "PDF にする画像がありません。", "没有要做成 PDF 的图片。", "沒有要做成 PDF 的圖片。", "PDF 로 만들 이미지가 없습니다.", "Aucune image à mettre dans un PDF.", "Keine Bilder für ein PDF."],
    "Err_ImagesPageSize": ["Unknown page size “{0}”: use a4, letter or image.", "ページの大きさ「{0}」は使えません。a4・letter・image のどれかにしてください。", "未知的页面大小“{0}”：请用 a4、letter 或 image。", "未知的頁面大小「{0}」：請用 a4、letter 或 image。", "알 수 없는 페이지 크기 “{0}”: a4, letter, image 중 하나를 쓰세요.", "Taille de page inconnue « {0} » : utilisez a4, letter ou image.", "Unbekannte Seitengröße „{0}“: verwenden Sie a4, letter oder image."],
    "Err_ImagesMargin": ["The margin must be 0 to 144 points and the resolution 36 to 1200 dpi.", "余白は 0〜144 ポイント、解像度は 36〜1200 dpi にしてください。", "边距必须在 0 到 144 点之间，分辨率在 36 到 1200 dpi 之间。", "邊界必須在 0 到 144 點之間，解析度在 36 到 1200 dpi 之間。", "여백은 0~144포인트, 해상도는 36~1200dpi여야 합니다.", "La marge doit être de 0 à 144 points et la résolution de 36 à 1200 ppp.", "Der Rand muss 0 bis 144 Punkt und die Auflösung 36 bis 1200 dpi betragen."],
    "Err_ImageUnsupported": ["{0} is not an image Washi can read (JPEG, PNG, WebP, BMP, GIF, HEIC).", "{0} は Washi が読める画像（JPEG・PNG・WebP・BMP・GIF・HEIC）ではありません。", "{0} 不是 Washi 能读取的图片（JPEG、PNG、WebP、BMP、GIF、HEIC）。", "{0} 不是 Washi 能讀取的圖片（JPEG、PNG、WebP、BMP、GIF、HEIC）。", "{0}은(는) Washi가 읽을 수 있는 이미지(JPEG, PNG, WebP, BMP, GIF, HEIC)가 아닙니다.", "{0} n’est pas une image que Washi sait lire (JPEG, PNG, WebP, BMP, GIF, HEIC).", "{0} ist kein Bild, das Washi lesen kann (JPEG, PNG, WebP, BMP, GIF, HEIC)."],
    "Err_ImageUnreadable": ["{0} could not be read as an image.", "{0} を画像として読み取れませんでした。", "无法把 {0} 当作图片读取。", "無法把 {0} 當作圖片讀取。", "{0}을(를) 이미지로 읽을 수 없습니다.", "{0} n’a pas pu être lu comme une image.", "{0} ließ sich nicht als Bild lesen."],
    "Err_ImagesCheck": ["Page {0} did not look like {1}, so the PDF was not kept.", "{0} ページが {1} と同じに見えなかったため、PDF を保存しませんでした。", "第 {0} 页与 {1} 不一致，因此没有保留 PDF。", "第 {0} 頁與 {1} 不一致，因此沒有保留 PDF。", "{0}쪽이 {1}과(와) 달라 PDF 를 저장하지 않았습니다.", "La page {0} ne ressemblait pas à {1} ; le PDF n’a pas été conservé.", "Seite {0} sah nicht aus wie {1}; das PDF wurde nicht behalten."],
    "Err_NotFound": ["{0} does not exist.", "{0} がありません。", "{0} 不存在。", "{0} 不存在。", "{0}이(가) 없습니다.", "{0} n’existe pas.", "{0} existiert nicht."],
    "Ui_ToolCompare": ["Compare", "比較", "比较", "比較", "비교", "Comparer", "Vergleichen"],
    "Ui_CompareTitle": ["Compare versions", "版の比較", "比较版本", "比較版本", "버전 비교", "Comparer des versions", "Versionen vergleichen"],
    "Ui_CompareLead": ["Compare the open file with another version of it: changed, added and removed words, added and removed pages, and changes to pictures and drawings.", "開いているファイルを別の版と比べます。変わった語句・増えた語句・消えた語句、増えたページ・消えたページ、図や画像の変化がわかります。", "将打开的文件与它的另一个版本比较：修改、新增和删除的文字，新增和删除的页面，以及图片和图形的变化。", "將開啟的檔案與它的另一個版本比較：修改、新增和刪除的文字，新增和刪除的頁面，以及圖片和圖形的變化。", "열린 파일을 다른 버전과 비교합니다. 바뀐 단어, 추가·삭제된 단어, 추가·삭제된 페이지, 그림과 도형의 변화를 보여 줍니다.", "Comparez le fichier ouvert avec une autre version : mots modifiés, ajoutés et supprimés, pages ajoutées et supprimées, changements d’images et de dessins.", "Vergleichen Sie die geöffnete Datei mit einer anderen Version: geänderte, hinzugefügte und entfernte Wörter, hinzugefügte und entfernte Seiten, Änderungen an Bildern und Zeichnungen."],
    "Ui_CompareChoose": ["Choose the other version…", "比べる版を選ぶ…", "选择另一个版本…", "選擇另一個版本…", "비교할 버전 선택…", "Choisir l’autre version…", "Andere Version wählen…"],
    "Ui_CompareOther": ["Compared with: {0}", "比較対象：{0}", "比较对象：{0}", "比較對象：{0}", "비교 대상: {0}", "Comparé avec : {0}", "Verglichen mit: {0}"],
    "Ui_CompareOtherNewer": ["That file is the newer version", "そちらが新しい版", "那个文件是较新的版本", "那個檔案是較新的版本", "그 파일이 새 버전", "Ce fichier est la version la plus récente", "Diese Datei ist die neuere Version"],
    "Ui_CompareRun": ["Compare", "比較する", "比较", "比較", "비교", "Comparer", "Vergleichen"],
    "Ui_CompareNone": ["No differences: the two versions have the same text and look the same.", "違いはありません。2 つの版の文字も見た目も同じです。", "没有差异：两个版本的文字和外观都相同。", "沒有差異：兩個版本的文字和外觀都相同。", "차이가 없습니다. 두 버전의 글자와 모양이 같습니다.", "Aucune différence : les deux versions ont le même texte et le même aspect.", "Keine Unterschiede: beide Versionen haben denselben Text und sehen gleich aus."],
    "Ui_CompareSummary": ["{0} difference(s): {1} changed, {2} added, {3} removed, {4} outside the text, {5} page(s) added, {6} removed.", "違い {0} 件：変更 {1}、追加 {2}、削除 {3}、文字以外 {4}、ページ追加 {5}、ページ削除 {6}。", "{0} 处差异：修改 {1}，新增 {2}，删除 {3}，文字以外 {4}，新增页面 {5}，删除页面 {6}。", "{0} 處差異：修改 {1}，新增 {2}，刪除 {3}，文字以外 {4}，新增頁面 {5}，刪除頁面 {6}。", "차이 {0}개: 변경 {1}, 추가 {2}, 삭제 {3}, 글자 외 {4}, 페이지 추가 {5}, 페이지 삭제 {6}.", "{0} différence(s) : {1} modifiée(s), {2} ajoutée(s), {3} supprimée(s), {4} hors du texte, {5} page(s) ajoutée(s), {6} supprimée(s).", "{0} Unterschied(e): {1} geändert, {2} hinzugefügt, {3} entfernt, {4} außerhalb des Texts, {5} Seite(n) hinzugefügt, {6} entfernt."],
    "Ui_CompareLegend": ["Green: added · yellow: changed · red: removed · purple: pictures and drawings", "緑：追加 · 黄：変更 · 赤：削除 · 紫：図や画像", "绿：新增 · 黄：修改 · 红：删除 · 紫：图片和图形", "綠：新增 · 黃：修改 · 紅：刪除 · 紫：圖片和圖形", "초록: 추가 · 노랑: 변경 · 빨강: 삭제 · 보라: 그림과 도형", "Vert : ajouté · jaune : modifié · rouge : supprimé · violet : images et dessins", "Grün: hinzugefügt · Gelb: geändert · Rot: entfernt · Lila: Bilder und Zeichnungen"],
    "Ui_CompareSave": ["Save marked copy…", "注釈付きコピーを保存…", "另存带标记的副本…", "另存帶標記的副本…", "표시한 사본 저장…", "Enregistrer une copie annotée…", "Markierte Kopie speichern…"],
    "Ui_CompareSaveNote": ["A copy of the newer version with each difference marked as a comment (the old wording in it), to send or review. The files compared are not changed.", "新しい版のコピーに、違いごとの注釈（元の文言入り）を付けます。送付や確認用です。比べたファイルは変わりません。", "在较新版本的副本上为每处差异加上批注（附原文），便于发送或审阅。比较的文件不会改变。", "在較新版本的副本上為每處差異加上註解（附原文），便於傳送或審閱。比較的檔案不會改變。", "새 버전의 사본에 차이마다 주석(원래 문구 포함)을 답니다. 보내거나 검토할 때 쓰세요. 비교한 파일은 바뀌지 않습니다.", "Une copie de la version récente où chaque différence est annotée (avec l’ancien libellé), à envoyer ou relire. Les fichiers comparés ne sont pas modifiés.", "Eine Kopie der neueren Version, in der jeder Unterschied als Kommentar markiert ist (mit dem alten Wortlaut), zum Versenden oder Prüfen. Die verglichenen Dateien bleiben unverändert."],
    "Ui_CompareMarked": ["Saved {0}: {1} comment(s) mark the differences.", "{0} を保存しました：{1} 件の注釈で違いを示しています。", "已保存 {0}：用 {1} 条批注标出差异。", "已儲存 {0}：用 {1} 則註解標出差異。", "{0} 저장: 주석 {1}개로 차이를 표시했습니다.", "{0} enregistré : {1} commentaire(s) signalent les différences.", "{0} gespeichert: {1} Kommentar(e) markieren die Unterschiede."],
    "Ui_CompareSameFile": ["Choose another file: that is the one already open.", "別のファイルを選んでください（それは開いているファイルです）。", "请选择另一个文件：那是已经打开的文件。", "請選擇另一個檔案：那是已經開啟的檔案。", "다른 파일을 고르세요. 그것은 이미 열린 파일입니다.", "Choisissez un autre fichier : c’est celui qui est déjà ouvert.", "Wählen Sie eine andere Datei: Das ist die bereits geöffnete."],
    "Ui_CmpWhere": ["Page {0}", "{0} ページ", "第 {0} 页", "第 {0} 頁", "{0}페이지", "Page {0}", "Seite {0}"],
    "Ui_CmpRowChanged": ["“{0}” → “{1}”", "「{0}」→「{1}」", "“{0}” → “{1}”", "「{0}」→「{1}」", "“{0}” → “{1}”", "« {0} » → « {1} »", "„{0}“ → „{1}“"],
    "Ui_CmpRowAdded": ["Added “{0}”", "追加「{0}」", "新增“{0}”", "新增「{0}」", "추가 “{0}”", "Ajouté « {0} »", "Hinzugefügt „{0}“"],
    "Ui_CmpRowDeleted": ["Removed “{0}”", "削除「{0}」", "删除“{0}”", "刪除「{0}」", "삭제 “{0}”", "Supprimé « {0} »", "Entfernt „{0}“"],
    "Ui_CmpRowVisual": ["Changed outside the text (picture or drawing)", "文字以外が変化（図や画像）", "文字以外有变化（图片或图形）", "文字以外有變化（圖片或圖形）", "글자 외 변화 (그림 또는 도형)", "Modifié hors du texte (image ou dessin)", "Außerhalb des Texts geändert (Bild oder Zeichnung)"],
    "Ui_CmpRowPageAdded": ["New page", "新しいページ", "新页面", "新頁面", "새 페이지", "Nouvelle page", "Neue Seite"],
    "Ui_CmpRowPageRemoved": ["Page {0} of the old version was removed", "旧版の {0} ページが削除されました", "旧版第 {0} 页被删除", "舊版第 {0} 頁被刪除", "이전 버전의 {0}페이지가 삭제됨", "La page {0} de l’ancienne version a été supprimée", "Seite {0} der alten Version wurde entfernt"],
    "Cmp_Size": ["{0} × {1} pt", "{0} × {1} pt", "{0} × {1} pt", "{0} × {1} pt", "{0} × {1} pt", "{0} × {1} pt", "{0} × {1} pt"],
    "Cmp_NoteAdded": ["Added: {0}", "追加：{0}", "新增：{0}", "新增：{0}", "추가: {0}", "Ajouté : {0}", "Hinzugefügt: {0}"],
    "Cmp_NoteChanged": ["Was: {0}", "変更前：{0}", "原为：{0}", "原為：{0}", "변경 전: {0}", "Avant : {0}", "Vorher: {0}"],
    "Cmp_NoteDeleted": ["Removed: {0}", "削除：{0}", "删除：{0}", "刪除：{0}", "삭제: {0}", "Supprimé : {0}", "Entfernt: {0}"],
    "Cmp_NoteVisual": ["Changed here (picture or drawing)", "ここが変化（図や画像）", "此处有变化（图片或图形）", "此處有變化（圖片或圖形）", "여기가 바뀜 (그림 또는 도형)", "Modifié ici (image ou dessin)", "Hier geändert (Bild oder Zeichnung)"],
    "Cmp_NoteSize": ["Page size changed from {0} to {1}", "ページの大きさが {0} から {1} に変化", "页面大小从 {0} 变为 {1}", "頁面大小從 {0} 變為 {1}", "페이지 크기가 {0}에서 {1}(으)로 바뀜", "Taille de page passée de {0} à {1}", "Seitengröße von {0} auf {1} geändert"],
    "Cmp_NotePageAdded": ["This page is new.", "このページは新しく追加されました。", "此页为新增。", "此頁為新增。", "새로 추가된 페이지입니다.", "Cette page est nouvelle.", "Diese Seite ist neu."],
    "Cmp_NotePageRemoved": ["Page {0} of the old version was removed: {1}", "旧版の {0} ページが削除されました：{1}", "旧版第 {0} 页被删除：{1}", "舊版第 {0} 頁被刪除：{1}", "이전 버전의 {0}페이지가 삭제됨: {1}", "La page {0} de l’ancienne version a été supprimée : {1}", "Seite {0} der alten Version wurde entfernt: {1}"],
    "Pdf_CompareLine": ["  old p.{0} / new p.{1}: {2}", "  旧 {0} / 新 {1} ページ：{2}", "  旧第 {0} 页 / 新第 {1} 页：{2}", "  舊第 {0} 頁 / 新第 {1} 頁：{2}", "  이전 {0} / 새 {1}페이지: {2}", "  anc. p. {0} / nouv. p. {1} : {2}", "  alt S. {0} / neu S. {1}: {2}"],
    "Err_CompareSame": ["The two versions are the same: there is nothing to mark.", "2 つの版は同じです。示す違いがありません。", "两个版本相同：没有要标记的内容。", "兩個版本相同：沒有要標記的內容。", "두 버전이 같아 표시할 것이 없습니다.", "Les deux versions sont identiques : rien à signaler.", "Beide Versionen sind gleich: Es gibt nichts zu markieren."],
    "Ui_PagesScopeAll": ["Crop and images: every page (select pages below for some only).", "切り抜きと画像：すべてのページ（一部だけなら下でページを選択）。", "裁剪和图片：所有页面（只处理部分页面时请在下方选择）。", "裁切和圖片：所有頁面（只處理部分頁面時請在下方選擇）。", "자르기와 이미지: 모든 페이지 (일부만 하려면 아래에서 페이지 선택).", "Rognage et images : toutes les pages (sélectionnez-en ci-dessous pour n’en traiter que certaines).", "Zuschneiden und Bilder: alle Seiten (für einige unten Seiten auswählen)."],
    "Ui_PagesScopeSome": ["Crop and images: pages {0}.", "切り抜きと画像：{0} ページ。", "裁剪和图片：第 {0} 页。", "裁切和圖片：第 {0} 頁。", "자르기와 이미지: {0}페이지.", "Rognage et images : pages {0}.", "Zuschneiden und Bilder: Seiten {0}."],
    "Ui_CropTitle": ["Crop pages", "ページの切り抜き", "裁剪页面", "裁切頁面", "페이지 자르기", "Rogner les pages", "Seiten zuschneiden"],
    "Ui_CropTop": ["Top", "上", "上", "上", "위", "Haut", "Oben"],
    "Ui_CropBottom": ["Bottom", "下", "下", "下", "아래", "Bas", "Unten"],
    "Ui_CropLeft": ["Left", "左", "左", "左", "왼쪽", "Gauche", "Links"],
    "Ui_CropRight": ["Right", "右", "右", "右", "오른쪽", "Droite", "Rechts"],
    "Ui_CropMm": ["Margins to cut off, in millimetres, on the page as shown.", "切り落とす余白（mm、表示どおりの向き）。", "要裁掉的边距（毫米，按显示方向）。", "要裁掉的邊界（公釐，依顯示方向）。", "잘라낼 여백 (mm, 보이는 방향 기준).", "Marges à couper, en millimètres, sur la page telle qu’affichée.", "Abzuschneidende Ränder in Millimetern, auf der Seite wie angezeigt."],
    "Ui_CropTrim": ["Trim to the content instead (cut the white paper around it)", "代わりに内容に合わせて切り詰める（周りの白い余白を切る）", "改为按内容裁剪（裁掉周围的空白）", "改為依內容裁切（裁掉周圍的空白）", "대신 내용에 맞춰 자르기 (주변의 흰 여백 자르기)", "Rogner plutôt au contenu (couper le papier blanc autour)", "Stattdessen auf den Inhalt zuschneiden (weißes Papier ringsum abschneiden)"],
    "Ui_CropRun": ["Crop and save copy…", "切り抜いてコピーを保存…", "裁剪并另存副本…", "裁切並另存副本…", "자르고 사본 저장…", "Rogner et enregistrer une copie…", "Zuschneiden und Kopie speichern…"],
    "Ui_Cropped": ["Saved {0}: {1} page(s) cropped. Checked: each looks like the part of the page it was cropped to.", "{0} を保存しました：{1} ページを切り抜き。確認済み：各ページが元の該当部分と同じに見えます。", "已保存 {0}：裁剪了 {1} 页。已检查：每页都与原页对应部分一致。", "已儲存 {0}：裁切了 {1} 頁。已檢查：每頁都與原頁對應部分一致。", "{0} 저장: {1}페이지 자름. 확인: 각 페이지가 원래의 해당 부분과 같습니다.", "{0} enregistré : {1} page(s) rognée(s). Vérifié : chacune ressemble à la partie gardée.", "{0} gespeichert: {1} Seite(n) zugeschnitten. Geprüft: jede sieht aus wie der behaltene Teil."],
    "Ui_ExportTitle": ["Save pages as images", "ページを画像として保存", "将页面存为图片", "將頁面存為圖片", "페이지를 이미지로 저장", "Enregistrer les pages en images", "Seiten als Bilder speichern"],
    "Ui_ExportRun": ["Choose a folder and save…", "フォルダーを選んで保存…", "选择文件夹并保存…", "選擇資料夾並儲存…", "폴더를 골라 저장…", "Choisir un dossier et enregistrer…", "Ordner wählen und speichern…"],
    "Ui_Exported": ["Saved {0} image(s) in {1}.", "{1} に {0} 枚の画像を保存しました。", "已在 {1} 保存 {0} 张图片。", "已在 {1} 儲存 {0} 張圖片。", "{1}에 이미지 {0}장을 저장했습니다.", "{0} image(s) enregistrée(s) dans {1}.", "{0} Bild(er) in {1} gespeichert."],
    "Pdf_Exported": ["Saved {0} image(s) in {1}; each was read back.", "{1} に {0} 枚の画像を保存しました（それぞれ読み戻して確認）。", "已在 {1} 保存 {0} 张图片（均已读回检查）。", "已在 {1} 儲存 {0} 張圖片（均已讀回檢查）。", "{1}에 이미지 {0}장을 저장했습니다 (각각 다시 읽어 확인).", "{0} image(s) enregistrée(s) dans {1} ; chacune relue.", "{0} Bild(er) in {1} gespeichert; jedes wurde zurückgelesen."],
    "Pdf_Cropped": ["Saved {0}: {1} page(s) cropped. Checked: each looks like the part of the page it was cropped to.", "{0} を保存しました：{1} ページを切り抜き。確認済み：各ページが元の該当部分と同じに見えます。", "已保存 {0}：裁剪了 {1} 页。已检查：每页都与原页对应部分一致。", "已儲存 {0}：裁切了 {1} 頁。已檢查：每頁都與原頁對應部分一致。", "{0} 저장: {1}페이지 자름. 확인: 각 페이지가 원래의 해당 부분과 같습니다.", "{0} enregistré : {1} page(s) rognée(s). Vérifié : chacune ressemble à la partie gardée.", "{0} gespeichert: {1} Seite(n) zugeschnitten. Geprüft: jede sieht aus wie der behaltene Teil."],
    "Err_ExportFormat": ["Unknown image format “{0}”: use png or jpeg.", "画像形式「{0}」は使えません。png か jpeg にしてください。", "未知的图片格式“{0}”：请用 png 或 jpeg。", "未知的圖片格式「{0}」：請用 png 或 jpeg。", "알 수 없는 이미지 형식 “{0}”: png 또는 jpeg를 쓰세요.", "Format d’image inconnu « {0} » : utilisez png ou jpeg.", "Unbekanntes Bildformat „{0}“: verwenden Sie png oder jpeg."],
    "Err_ExportDpi": ["The resolution must be 36 to 600 dpi.", "解像度は 36〜600 dpi にしてください。", "分辨率必须在 36 到 600 dpi 之间。", "解析度必須在 36 到 600 dpi 之間。", "해상도는 36~600dpi여야 합니다.", "La résolution doit être de 36 à 600 ppp.", "Die Auflösung muss 36 bis 600 dpi betragen."],
    "Err_ExportQuality": ["The JPEG quality must be 1 to 100.", "JPEG の品質は 1〜100 にしてください。", "JPEG 质量必须在 1 到 100 之间。", "JPEG 品質必須在 1 到 100 之間。", "JPEG 품질은 1~100이어야 합니다.", "La qualité JPEG doit être de 1 à 100.", "Die JPEG-Qualität muss 1 bis 100 betragen."],
    "Err_ExportCheck": ["{0} did not read back, so nothing was kept.", "{0} を読み戻せなかったため、何も残しませんでした。", "{0} 无法读回，因此没有保留任何文件。", "{0} 無法讀回，因此沒有保留任何檔案。", "{0}을(를) 다시 읽을 수 없어 아무것도 남기지 않았습니다.", "{0} ne se relisait pas ; rien n’a été conservé.", "{0} ließ sich nicht zurücklesen; nichts wurde behalten."],
    "Err_CropNothing": ["Nothing to crop: give margins or trim.", "切り抜くものがありません。余白を指定するか、切り詰めを選んでください。", "没有要裁剪的内容：请指定边距或按内容裁剪。", "沒有要裁切的內容：請指定邊界或依內容裁切。", "자를 것이 없습니다. 여백을 지정하거나 내용에 맞춰 자르기를 고르세요.", "Rien à rogner : indiquez des marges ou rognez au contenu.", "Nichts zuzuschneiden: Ränder angeben oder auf den Inhalt zuschneiden."],
    "Err_CropNegative": ["Margins cannot be negative.", "余白は負の値にできません。", "边距不能为负数。", "邊界不能為負數。", "여백은 음수일 수 없습니다.", "Les marges ne peuvent pas être négatives.", "Ränder können nicht negativ sein."],
    "Err_CropMargins": ["--margins takes 1, 2 or 4 numbers (points).", "--margins には 1・2・4 個の数（ポイント）を指定します。", "--margins 需要 1、2 或 4 个数字（点）。", "--margins 需要 1、2 或 4 個數字（點）。", "--margins에는 숫자(포인트) 1, 2 또는 4개를 씁니다.", "--margins prend 1, 2 ou 4 nombres (points).", "--margins nimmt 1, 2 oder 4 Zahlen (Punkt)."],
    "Err_CropTooSmall": ["Page {0} would be smaller than half an inch.", "{0} ページが半インチより小さくなってしまいます。", "第 {0} 页会小于半英寸。", "第 {0} 頁會小於半英寸。", "{0}페이지가 반 인치보다 작아집니다.", "La page {0} ferait moins d’un demi-pouce.", "Seite {0} würde kleiner als ein halber Zoll."],
    "Err_CropBlank": ["The pages are blank: there is nothing to trim to.", "ページが白紙のため、切り詰める対象がありません。", "页面是空白的：没有可按内容裁剪的东西。", "頁面是空白的：沒有可依內容裁切的東西。", "페이지가 비어 있어 내용에 맞춰 자를 것이 없습니다.", "Les pages sont blanches : rien à quoi rogner.", "Die Seiten sind leer: Es gibt nichts, worauf zugeschnitten werden kann."],
    "Err_CropCheck": ["Page {0} did not look like the part of the original it was cropped to, so the copy was not kept.", "{0} ページが元の該当部分と同じに見えなかったため、保存しませんでした。", "第 {0} 页与原页对应部分不一致，因此没有保留副本。", "第 {0} 頁與原頁對應部分不一致，因此沒有保留副本。", "{0}페이지가 원래의 해당 부분과 달라 사본을 저장하지 않았습니다.", "La page {0} ne ressemblait pas à la partie gardée ; la copie n’a pas été conservée.", "Seite {0} sah nicht aus wie der behaltene Teil; die Kopie wurde nicht behalten."],
    "Ui_ToolLinks": ["Links", "リンク", "链接", "連結", "링크", "Liens", "Links"],
    "Ui_LinksTitle": ["Links", "リンク", "链接", "連結", "링크", "Liens", "Links"],
    "Ui_LinksLead": ["Drag a box on the page and type where it goes: a web or mail address, or a page number. Or turn the addresses written in the text into links. The pages look the same.", "ページ上で枠を描き、リンク先（URL・メールアドレス・ページ番号）を入力します。本文に書かれたアドレスをまとめてリンクにすることもできます。ページの見た目は変わりません。", "在页面上拖出一个框，输入链接目标：网址、邮箱或页码。也可以把正文中写的地址一次变成链接。页面外观不变。", "在頁面上拖出一個框，輸入連結目標：網址、郵件地址或頁碼。也可以把內文中寫的地址一次變成連結。頁面外觀不變。", "페이지에 상자를 끌어 그리고 이동할 곳(웹·메일 주소 또는 쪽 번호)을 입력하세요. 본문에 적힌 주소를 한꺼번에 링크로 만들 수도 있습니다. 페이지 모양은 그대로입니다.", "Tracez un cadre sur la page et tapez sa cible : une adresse web ou mail, ou un numéro de page. Ou transformez les adresses écrites dans le texte en liens. Les pages gardent le même aspect.", "Ziehen Sie einen Rahmen auf der Seite und geben Sie das Ziel ein: eine Web- oder Mailadresse oder eine Seitenzahl. Oder machen Sie die im Text stehenden Adressen zu Links. Die Seiten sehen gleich aus."],
    "Ui_LinkNew": ["Where does this link go?", "このリンクの行き先", "这个链接指向哪里？", "這個連結指向哪裡？", "이 링크는 어디로 갑니까?", "Où mène ce lien ?", "Wohin führt dieser Link?"],
    "Ui_LinkTargetHint": ["https://…, name@example.com or a page number", "https://…、name@example.com、またはページ番号", "https://…、name@example.com 或页码", "https://…、name@example.com 或頁碼", "https://…, name@example.com 또는 쪽 번호", "https://…, nom@example.com ou un numéro de page", "https://…, name@example.com oder eine Seitenzahl"],
    "Ui_LinksFind": ["Link the addresses in the text", "本文中のアドレスをリンクにする", "把正文中的地址变成链接", "把內文中的地址變成連結", "본문의 주소를 링크로 만들기", "Lier les adresses du texte", "Adressen im Text verlinken"],
    "Ui_LinksFound": ["{0} address(es) found: they will become links.", "{0} 件のアドレスが見つかりました。リンクにします。", "找到 {0} 个地址：将变成链接。", "找到 {0} 個地址：將變成連結。", "주소 {0}개를 찾았습니다. 링크로 만듭니다.", "{0} adresse(s) trouvée(s) : elles deviendront des liens.", "{0} Adresse(n) gefunden: sie werden zu Links."],
    "Ui_LinksNoneFound": ["No web or mail address without a link was found in the text.", "リンクのない URL やメールアドレスは本文に見つかりませんでした。", "正文中没有找到未加链接的网址或邮箱。", "內文中沒有找到未加連結的網址或郵件地址。", "본문에서 링크가 없는 웹·메일 주소를 찾지 못했습니다.", "Aucune adresse web ou mail sans lien n’a été trouvée dans le texte.", "Im Text wurde keine Web- oder Mailadresse ohne Link gefunden."],
    "Ui_LinksNew": ["New links", "追加するリンク", "新链接", "新連結", "새 링크", "Nouveaux liens", "Neue Links"],
    "Ui_LinksExisting": ["Links in the file", "ファイル内のリンク", "文件中的链接", "檔案中的連結", "파일의 링크", "Liens du fichier", "Links in der Datei"],
    "Ui_LinksNone": ["The file has no links.", "このファイルにはリンクがありません。", "文件中没有链接。", "檔案中沒有連結。", "파일에 링크가 없습니다.", "Le fichier n’a aucun lien.", "Die Datei hat keine Links."],
    "Ui_LinkRemove": ["Remove", "削除", "删除", "刪除", "삭제", "Retirer", "Entfernen"],
    "Ui_LinkWhere": ["Page {0}", "{0} ページ", "第 {0} 页", "第 {0} 頁", "{0}페이지", "Page {0}", "Seite {0}"],
    "Ui_LinkToPage": ["To page {0}", "{0} ページへ", "跳到第 {0} 页", "跳到第 {0} 頁", "{0}페이지로", "Vers la page {0}", "Zu Seite {0}"],
    "Ui_LinkOther": ["Another kind of link (kept as it is)", "その他のリンク（そのまま残します）", "其他类型的链接（保持原样）", "其他類型的連結（保持原樣）", "다른 종류의 링크 (그대로 둠)", "Autre type de lien (gardé tel quel)", "Andere Art Link (bleibt, wie er ist)"],
    "Ui_LinksSave": ["Save {0} change(s) as copy…", "{0} 件の変更をコピーに保存…", "将 {0} 处修改另存为副本…", "將 {0} 處修改另存為副本…", "변경 {0}개를 사본으로 저장…", "Enregistrer {0} modification(s) dans une copie…", "{0} Änderung(en) als Kopie speichern…"],
    "Ui_LinksSaveNote": ["Links have no border: the pages look the same. The copy is checked before it is kept; the original is not changed.", "リンクには枠線を付けないので、ページの見た目は変わりません。保存前にコピーを確認します。元のファイルは変わりません。", "链接没有边框，页面外观不变。保存前会检查副本；原文件不会改变。", "連結沒有邊框，頁面外觀不變。儲存前會檢查副本；原檔案不會改變。", "링크에는 테두리가 없어 페이지 모양이 그대로입니다. 저장하기 전에 사본을 확인하며 원본은 바뀌지 않습니다.", "Les liens n’ont pas de bordure : les pages gardent le même aspect. La copie est vérifiée avant d’être gardée ; l’original n’est pas modifié.", "Links haben keinen Rahmen: die Seiten sehen gleich aus. Die Kopie wird vor dem Behalten geprüft; das Original bleibt unverändert."],
    "Ui_LinksSaved": ["Saved {0}: {1} link(s) added, {2} removed. Checked: the pages look the same.", "{0} を保存しました：リンクを {1} 件追加、{2} 件削除。確認済み：ページの見た目は同じです。", "已保存 {0}：新增 {1} 个链接，删除 {2} 个。已检查：页面外观相同。", "已儲存 {0}：新增 {1} 個連結，刪除 {2} 個。已檢查：頁面外觀相同。", "{0} 저장: 링크 {1}개 추가, {2}개 삭제. 확인: 페이지 모양이 같습니다.", "{0} enregistré : {1} lien(s) ajouté(s), {2} retiré(s). Vérifié : les pages ont le même aspect.", "{0} gespeichert: {1} Link(s) hinzugefügt, {2} entfernt. Geprüft: die Seiten sehen gleich aus."],
    "Pdf_LinkLine": ["  {0}  page {1}: {2}  [{3}]", "  {0}  {1} ページ：{2}  [{3}]", "  {0}  第 {1} 页：{2}  [{3}]", "  {0}  第 {1} 頁：{2}  [{3}]", "  {0}  {1}페이지: {2}  [{3}]", "  {0}  page {1} : {2}  [{3}]", "  {0}  Seite {1}: {2}  [{3}]"],
    "Err_LinkTarget": ["“{0}” is not a web address, a mail address or a page number.", "「{0}」は URL・メールアドレス・ページ番号のどれでもありません。", "“{0}”不是网址、邮箱或页码。", "「{0}」不是網址、郵件地址或頁碼。", "“{0}”은(는) 웹 주소, 메일 주소, 쪽 번호가 아닙니다.", "« {0} » n’est ni une adresse web, ni une adresse mail, ni un numéro de page.", "„{0}“ ist weder eine Web- noch eine Mailadresse noch eine Seitenzahl."],
    "Err_LinkNothing": ["No link to add, change or remove.", "追加・変更・削除するリンクがありません。", "没有要添加、修改或删除的链接。", "沒有要新增、修改或刪除的連結。", "추가·변경·삭제할 링크가 없습니다.", "Aucun lien à ajouter, modifier ou retirer.", "Kein Link zum Hinzufügen, Ändern oder Entfernen."],
    "Err_LinkId": ["There is no link {0}.", "リンク {0} はありません。", "没有链接 {0}。", "沒有連結 {0}。", "링크 {0}이(가) 없습니다.", "Il n’y a pas de lien {0}.", "Es gibt keinen Link {0}."],
    "Err_LinkAdd": ["--add takes page:x0,y0,x1,y1=target.", "--add には page:x0,y0,x1,y1=リンク先 を指定します。", "--add 的格式是 page:x0,y0,x1,y1=目标。", "--add 的格式是 page:x0,y0,x1,y1=目標。", "--add 형식은 page:x0,y0,x1,y1=대상 입니다.", "--add prend page:x0,y0,x1,y1=cible.", "--add nimmt page:x0,y0,x1,y1=Ziel."],
    "Err_LinkCheck": ["The link to {0} on page {1} did not read back, so the copy was not kept.", "{1} ページの {0} へのリンクが読み戻せなかったため、保存しませんでした。", "第 {1} 页指向 {0} 的链接无法读回，因此没有保留副本。", "第 {1} 頁指向 {0} 的連結無法讀回，因此沒有保留副本。", "{1}페이지의 {0} 링크를 다시 읽을 수 없어 사본을 저장하지 않았습니다.", "Le lien vers {0} page {1} ne se relisait pas ; la copie n’a pas été conservée.", "Der Link zu {0} auf Seite {1} ließ sich nicht lesen; die Kopie wurde nicht behalten."],
    "Err_LinkChanged": ["Page {0} looked different with its links, so the copy was not kept.", "{0} ページの見た目がリンクで変わったため、保存しませんでした。", "第 {0} 页加链接后外观改变，因此没有保留副本。", "第 {0} 頁加連結後外觀改變，因此沒有保留副本。", "{0}페이지가 링크 때문에 달라 보여 사본을 저장하지 않았습니다.", "La page {0} changeait d’aspect avec ses liens ; la copie n’a pas été conservée.", "Seite {0} sah mit den Links anders aus; die Kopie wurde nicht behalten."],
    "Ui_ToolImages": ["Images", "画像", "图片", "圖片", "이미지", "Images", "Bilder"],
    "Ui_ImagesTitle": ["Images on the page", "ページの画像", "页面上的图片", "頁面上的圖片", "페이지의 이미지", "Images de la page", "Bilder auf der Seite"],
    "Ui_ImagesLead": ["Click an image to replace it with a picture file (it keeps its own shape in the old place) or remove it. To add a picture, choose it and drag a box on the page.", "画像をクリックすると、画像ファイルに差し替え（元の場所に縦横比を保って収めます）または削除できます。追加するには画像を選んでページ上に枠を描きます。", "点击图片可以用图片文件替换（保持新图比例放在原位置）或删除。要添加图片，先选择文件再在页面上拖出一个框。", "點一下圖片可以用圖片檔取代（保持新圖比例放在原位置）或刪除。要新增圖片，先選擇檔案再在頁面上拖出一個框。", "이미지를 클릭하면 이미지 파일로 교체(원래 자리에 비율을 유지해 넣음)하거나 삭제할 수 있습니다. 추가하려면 이미지를 고르고 페이지에 상자를 그리세요.", "Cliquez sur une image pour la remplacer par un fichier (qui garde ses proportions à l’ancienne place) ou la retirer. Pour ajouter une image, choisissez-la et tracez un cadre sur la page.", "Klicken Sie auf ein Bild, um es durch eine Bilddatei zu ersetzen (sie behält ihr Seitenverhältnis am alten Platz) oder es zu entfernen. Zum Hinzufügen ein Bild wählen und einen Rahmen auf der Seite ziehen."],
    "Ui_ImagesAdd": ["Add a picture…", "画像を追加…", "添加图片…", "新增圖片…", "이미지 추가…", "Ajouter une image…", "Bild hinzufügen…"],
    "Ui_ImagePlace": ["Drag a box on the page where {0} goes.", "{0} を置く場所にページ上で枠を描いてください。", "请在页面上拖出放置 {0} 的框。", "請在頁面上拖出放置 {0} 的框。", "{0}을(를) 놓을 자리에 페이지에서 상자를 그리세요.", "Tracez sur la page le cadre où ira {0}.", "Ziehen Sie auf der Seite einen Rahmen, wo {0} hinkommt."],
    "Ui_ImageSelected": ["Selected image", "選んだ画像", "选中的图片", "選取的圖片", "선택한 이미지", "Image sélectionnée", "Ausgewähltes Bild"],
    "Ui_ImageReplace": ["Replace…", "差し替え…", "替换…", "取代…", "교체…", "Remplacer…", "Ersetzen…"],
    "Ui_ImageRemove": ["Remove", "削除", "删除", "刪除", "삭제", "Retirer", "Entfernen"],
    "Ui_ImageUndo": ["Leave as it is", "元のままにする", "保持原样", "保持原樣", "그대로 두기", "Laisser tel quel", "Unverändert lassen"],
    "Ui_ImagesOnPages": ["Images in the file", "ファイル内の画像", "文件中的图片", "檔案中的圖片", "파일의 이미지", "Images du fichier", "Bilder in der Datei"],
    "Ui_ImagesNone": ["The pages draw no images.", "ページに画像はありません。", "页面上没有图片。", "頁面上沒有圖片。", "페이지에 이미지가 없습니다.", "Les pages ne dessinent aucune image.", "Die Seiten zeichnen keine Bilder."],
    "Ui_ImageWhere": ["Page {0} · {1} × {2} px", "{0} ページ · {1} × {2} px", "第 {0} 页 · {1} × {2} px", "第 {0} 頁 · {1} × {2} px", "{0}페이지 · {1} × {2} px", "Page {0} · {1} × {2} px", "Seite {0} · {1} × {2} px"],
    "Ui_ImageWillGo": ["Will be removed", "削除します", "将删除", "將刪除", "삭제됨", "Sera retirée", "Wird entfernt"],
    "Ui_ImageWillBe": ["Will be replaced by {0}", "{0} に差し替えます", "将替换为 {0}", "將取代為 {0}", "{0}(으)로 교체됨", "Sera remplacée par {0}", "Wird durch {0} ersetzt"],
    "Ui_ImageAdd": ["Add {0} on page {1}", "{1} ページに {0} を追加", "在第 {1} 页添加 {0}", "在第 {1} 頁新增 {0}", "{1}페이지에 {0} 추가", "Ajouter {0} page {1}", "{0} auf Seite {1} hinzufügen"],
    "Ui_ImagesSave": ["Save {0} change(s) as copy…", "{0} 件の変更をコピーに保存…", "将 {0} 处修改另存为副本…", "將 {0} 處修改另存為副本…", "변경 {0}개를 사본으로 저장…", "Enregistrer {0} modification(s) dans une copie…", "{0} Änderung(en) als Kopie speichern…"],
    "Ui_ImagesSaveNote": ["Only the images change: the copy is rendered next to the original before it is kept. JPEG photos go in as they are. The original is not changed.", "変わるのは画像だけです。保存前にコピーを元と並べて確認します。JPEG はそのまま入れます。元のファイルは変わりません。", "只有图片会改变：保存前会把副本与原文件对照检查。JPEG 照片原样放入。原文件不会改变。", "只有圖片會改變：儲存前會把副本與原檔案對照檢查。JPEG 照片原樣放入。原檔案不會改變。", "이미지만 바뀝니다. 저장하기 전에 사본을 원본과 비교합니다. JPEG 사진은 그대로 넣습니다. 원본은 바뀌지 않습니다.", "Seules les images changent : la copie est comparée à l’original avant d’être gardée. Les JPEG sont insérés tels quels. L’original n’est pas modifié.", "Nur die Bilder ändern sich: die Kopie wird vor dem Behalten mit dem Original verglichen. JPEG-Fotos kommen unverändert hinein. Das Original bleibt unverändert."],
    "Ui_ImagesSaved": ["Saved {0}: {1} image(s) added, {2} replaced, {3} removed. Checked: nothing else changed.", "{0} を保存しました：画像を {1} 件追加、{2} 件差し替え、{3} 件削除。確認済み：ほかは変わっていません。", "已保存 {0}：新增 {1} 张图片，替换 {2} 张，删除 {3} 张。已检查：其他部分没有变化。", "已儲存 {0}：新增 {1} 張圖片，取代 {2} 張，刪除 {3} 張。已檢查：其他部分沒有變化。", "{0} 저장: 이미지 {1}개 추가, {2}개 교체, {3}개 삭제. 확인: 다른 부분은 바뀌지 않았습니다.", "{0} enregistré : {1} image(s) ajoutée(s), {2} remplacée(s), {3} retirée(s). Vérifié : rien d’autre n’a changé.", "{0} gespeichert: {1} Bild(er) hinzugefügt, {2} ersetzt, {3} entfernt. Geprüft: sonst hat sich nichts geändert."],
    "Pdf_ImageLine": ["  {0}  page {1}: {2} × {3} px  [{4}]", "  {0}  {1} ページ：{2} × {3} px  [{4}]", "  {0}  第 {1} 页：{2} × {3} px  [{4}]", "  {0}  第 {1} 頁：{2} × {3} px  [{4}]", "  {0}  {1}페이지: {2} × {3} px  [{4}]", "  {0}  page {1} : {2} × {3} px  [{4}]", "  {0}  Seite {1}: {2} × {3} px  [{4}]"],
    "Err_ImageEditNothing": ["No image to add, replace or remove.", "追加・差し替え・削除する画像がありません。", "没有要添加、替换或删除的图片。", "沒有要新增、取代或刪除的圖片。", "추가·교체·삭제할 이미지가 없습니다.", "Aucune image à ajouter, remplacer ou retirer.", "Kein Bild zum Hinzufügen, Ersetzen oder Entfernen."],
    "Err_ImageId": ["There is no image {0}.", "画像 {0} はありません。", "没有图片 {0}。", "沒有圖片 {0}。", "이미지 {0}이(가) 없습니다.", "Il n’y a pas d’image {0}.", "Es gibt kein Bild {0}."],
    "Err_ImageAdd": ["--add takes page:x0,y0,x1,y1=image file.", "--add には page:x0,y0,x1,y1=画像ファイル を指定します。", "--add 的格式是 page:x0,y0,x1,y1=图片文件。", "--add 的格式是 page:x0,y0,x1,y1=圖片檔。", "--add 형식은 page:x0,y0,x1,y1=이미지 파일 입니다.", "--add prend page:x0,y0,x1,y1=fichier image.", "--add nimmt page:x0,y0,x1,y1=Bilddatei."],
    "Err_ImageReplaceArg": ["--replace takes id=image file.", "--replace には id=画像ファイル を指定します。", "--replace 的格式是 id=图片文件。", "--replace 的格式是 id=圖片檔。", "--replace 형식은 id=이미지 파일 입니다.", "--replace prend id=fichier image.", "--replace nimmt id=Bilddatei."],
    "Err_ImageEditChanged": ["Page {0} changed outside the images, so the copy was not kept.", "{0} ページが画像以外の場所で変わったため、保存しませんでした。", "第 {0} 页在图片以外发生了变化，因此没有保留副本。", "第 {0} 頁在圖片以外發生了變化，因此沒有保留副本。", "{0}페이지가 이미지 밖에서 바뀌어 사본을 저장하지 않았습니다.", "La page {0} a changé hors des images ; la copie n’a pas été conservée.", "Seite {0} hat sich außerhalb der Bilder verändert; die Kopie wurde nicht behalten."],
    "Err_ImageEditCheck": ["{0} did not show on page {1} as it should, so the copy was not kept.", "{1} ページで {0} が正しく表示されなかったため、保存しませんでした。", "{0} 在第 {1} 页显示不正确，因此没有保留副本。", "{0} 在第 {1} 頁顯示不正確，因此沒有保留副本。", "{1}페이지에서 {0}이(가) 제대로 보이지 않아 사본을 저장하지 않았습니다.", "{0} ne s’affichait pas comme prévu page {1} ; la copie n’a pas été conservée.", "{0} wurde auf Seite {1} nicht richtig angezeigt; die Kopie wurde nicht behalten."],
    "Ui_StampExisting": ["This file has headers or footers on {0} page(s) and a watermark on {1} page(s), added by Washi or another program.", "このファイルには、ヘッダー・フッター（{0} ページ）と透かし（{1} ページ）があります（Washi または他のソフトが入れたもの）。", "此文件中有页眉页脚（{0} 页）和水印（{1} 页），由 Washi 或其他软件加入。", "此檔案中有頁首頁尾（{0} 頁）和浮水印（{1} 頁），由 Washi 或其他軟體加入。", "이 파일에는 머리글·바닥글({0}쪽)과 워터마크({1}쪽)가 있습니다 (Washi 또는 다른 프로그램이 넣은 것).", "Ce fichier a des en-têtes ou pieds de page sur {0} page(s) et un filigrane sur {1} page(s), ajoutés par Washi ou un autre programme.", "Diese Datei hat Kopf- oder Fußzeilen auf {0} Seite(n) und ein Wasserzeichen auf {1} Seite(n), hinzugefügt von Washi oder einem anderen Programm."],
    "Ui_StampReplace": ["Replace them with the new ones", "新しいものに置き換える", "用新的内容替换", "用新的內容取代", "새 것으로 바꾸기", "Les remplacer par les nouveaux", "Durch die neuen ersetzen"],
    "Ui_StampRemove": ["Remove them and save copy…", "外してコピーを保存…", "去掉并另存副本…", "去掉並另存副本…", "빼고 사본 저장…", "Les retirer et enregistrer une copie…", "Entfernen und Kopie speichern…"],
    "Ui_StampRemoved": ["Saved {0}: {1} header, footer or watermark block(s) were taken out. Checked: none is left, nothing else changed.", "{0} を保存しました：ヘッダー・フッター・透かしを {1} か所外しました。確認済み：残りはなく、ほかは変わっていません。", "已保存 {0}：去掉了 {1} 处页眉、页脚或水印。已检查：没有残留，其他部分没有变化。", "已儲存 {0}：去掉了 {1} 處頁首、頁尾或浮水印。已檢查：沒有殘留，其他部分沒有變化。", "{0} 저장: 머리글·바닥글·워터마크 {1}곳을 뺐습니다. 확인: 남은 것이 없고 다른 부분은 그대로입니다.", "{0} enregistré : {1} bloc(s) d’en-tête, de pied de page ou de filigrane retirés. Vérifié : il n’en reste aucun, rien d’autre n’a changé.", "{0} gespeichert: {1} Kopf-, Fußzeilen- oder Wasserzeichen-Block/Blöcke entfernt. Geprüft: keiner ist übrig, sonst hat sich nichts geändert."],
    "Err_StampNone": ["There are no headers, footers or watermarks marked as such in this file (ones drawn as ordinary text cannot be told apart).", "このファイルには、ヘッダー・フッター・透かしとして印の付いたものがありません（普通の文字として描かれたものは区別できません）。", "此文件中没有标记为页眉、页脚或水印的内容（作为普通文字绘制的无法区分）。", "此檔案中沒有標記為頁首、頁尾或浮水印的內容（以一般文字繪製的無法區分）。", "이 파일에는 머리글·바닥글·워터마크로 표시된 것이 없습니다 (일반 글자로 그려진 것은 구별할 수 없음).", "Ce fichier n’a ni en-tête, ni pied de page, ni filigrane marqué comme tel (ceux dessinés comme du texte ordinaire ne se distinguent pas).", "Diese Datei hat keine als solche markierten Kopf-, Fußzeilen oder Wasserzeichen (als normaler Text gezeichnete lassen sich nicht unterscheiden)."],
    "Err_StampRemoveCheck": ["Some of the marks were still there after removing them, so the copy was not kept.", "外したはずのものが残っていたため、保存しませんでした。", "去掉后仍有残留，因此没有保留副本。", "去掉後仍有殘留，因此沒有保留副本。", "뺀 뒤에도 남은 것이 있어 사본을 저장하지 않았습니다.", "Certaines marques étaient encore là après leur retrait ; la copie n’a pas été conservée.", "Einige Markierungen waren nach dem Entfernen noch da; die Kopie wurde nicht behalten."],
    "Err_StampRemoveArg": ["--remove takes headers, watermarks or all.", "--remove には headers・watermarks・all のどれかを指定します。", "--remove 的值为 headers、watermarks 或 all。", "--remove 的值為 headers、watermarks 或 all。", "--remove에는 headers, watermarks, all 중 하나를 씁니다.", "--remove prend headers, watermarks ou all.", "--remove nimmt headers, watermarks oder all."],
    "Ui_EditTextModeLines": ["Lines", "行", "行", "行", "줄", "Lignes", "Zeilen"],
    "Ui_EditTextModeParagraphs": ["Paragraphs", "段落", "段落", "段落", "문단", "Paragraphes", "Absätze"],
    "Ui_EditTextKeysParagraph": ["The text is set again in the paragraph's width, font and spacing. Enter starts a new line; ⌘/Ctrl+Enter keeps the paragraph, Esc leaves it as it was.", "段落の幅・フォント・行間のまま組み直します。Enter で改行、⌘/Ctrl+Enter で確定、Esc で元のまま。", "按段落原来的宽度、字体和行距重新排版。Enter 换行，⌘/Ctrl+Enter 确定，Esc 保持原样。", "依段落原來的寬度、字型和行距重新排版。Enter 換行，⌘/Ctrl+Enter 確定，Esc 保持原樣。", "문단의 너비, 글꼴, 줄 간격 그대로 다시 조판합니다. Enter는 줄바꿈, ⌘/Ctrl+Enter는 확정, Esc는 그대로 둡니다.", "Le texte est recomposé dans la largeur, la police et l’interligne du paragraphe. Entrée passe à la ligne ; ⌘/Ctrl+Entrée garde le paragraphe, Échap le laisse tel quel.", "Der Text wird in Breite, Schrift und Zeilenabstand des Absatzes neu gesetzt. Eingabe beginnt eine neue Zeile; ⌘/Strg+Eingabe übernimmt den Absatz, Esc lässt ihn, wie er war."],
    "Err_ParagraphTooLong": ["The new text needs {0} lines and the paragraph has room for {1} (it may grow only into empty space below it): shorten it.", "新しい文章は {0} 行必要ですが、入るのは {1} 行です（下の空いている所までしか広げられません）。短くしてください。", "新文字需要 {0} 行，段落只能容纳 {1} 行（只能向下扩展到空白处）：请缩短。", "新文字需要 {0} 行，段落只能容納 {1} 行（只能向下擴展到空白處）：請縮短。", "새 글은 {0}줄이 필요하지만 문단에는 {1}줄만 들어갑니다 (아래 빈 곳까지만 늘릴 수 있음). 줄이세요.", "Le nouveau texte demande {0} lignes et le paragraphe n’en contient que {1} (il ne peut s’étendre que dans l’espace libre en dessous) : raccourcissez-le.", "Der neue Text braucht {0} Zeilen, der Absatz hat Platz für {1} (er wächst nur in freien Raum darunter): kürzen Sie ihn."],
    "Err_ParagraphMixed": ["The paragraph mixes text sizes: edit its lines one at a time.", "段落内で文字の大きさが混ざっています。1 行ずつ編集してください。", "段落中混有不同字号：请逐行编辑。", "段落中混有不同字級：請逐行編輯。", "문단에 글자 크기가 섞여 있습니다. 한 줄씩 고치세요.", "Le paragraphe mélange des tailles de texte : modifiez ses lignes une à une.", "Der Absatz mischt Textgrößen: bearbeiten Sie seine Zeilen einzeln."],
    "Err_ParagraphNone": ["“{0}” is not in a paragraph that can be set again.", "「{0}」は組み直せる段落の中にありません。", "“{0}”不在可以重新排版的段落中。", "「{0}」不在可以重新排版的段落中。", "“{0}”은(는) 다시 조판할 수 있는 문단 안에 없습니다.", "« {0} » n’est pas dans un paragraphe recomposable.", "„{0}“ steht nicht in einem Absatz, der neu gesetzt werden kann."],
    "Err_ImageHeic": ["{0} is a HEIC photo and this computer has nothing to convert it with (macOS does; on Linux install libheif's heif-convert): save it as JPEG first.", "{0} は HEIC 写真ですが、このコンピューターには変換する手段がありません（macOS にはあります。Linux では libheif の heif-convert を入れてください）。先に JPEG で保存してください。", "{0} 是 HEIC 照片，但这台电脑没有转换工具（macOS 自带；Linux 请安装 libheif 的 heif-convert）：请先另存为 JPEG。", "{0} 是 HEIC 照片，但這台電腦沒有轉換工具（macOS 內建；Linux 請安裝 libheif 的 heif-convert）：請先另存為 JPEG。", "{0}은(는) HEIC 사진인데 이 컴퓨터에는 변환 도구가 없습니다 (macOS에는 있음, Linux는 libheif의 heif-convert 설치). 먼저 JPEG로 저장하세요.", "{0} est une photo HEIC et cet ordinateur n’a rien pour la convertir (macOS le peut ; sous Linux installez heif-convert de libheif) : enregistrez-la d’abord en JPEG.", "{0} ist ein HEIC-Foto, und dieser Computer kann es nicht umwandeln (macOS kann es; unter Linux heif-convert aus libheif installieren): zuerst als JPEG speichern."],
    "Err_FlattenNothing": ["Nothing to flatten: choose form fields, comments or both.", "確定する対象がありません。記入欄・コメントのどちらか（または両方）を選んでください。", "没有要拼合的内容：请选择表单栏位、注释或两者。", "沒有要平面化的內容：請選擇表單欄位、註解或兩者。", "병합할 대상이 없습니다. 입력란, 주석 또는 둘 다를 선택하세요.", "Rien à aplatir : choisissez les champs, les commentaires ou les deux.", "Nichts zu reduzieren: Formularfelder, Kommentare oder beides wählen."],
    "Err_FlattenNone": ["There are no form fields or comments to flatten.", "確定する記入欄やコメントがありません。", "没有可拼合的表单栏位或注释。", "沒有可平面化的表單欄位或註解。", "병합할 입력란이나 주석이 없습니다.", "Il n’y a ni champ ni commentaire à aplatir.", "Es gibt keine Formularfelder oder Kommentare zum Reduzieren."],
    "Err_FlattenSigned": ["The file is signed: flattening it would break its signatures.", "このファイルには署名があります。確定すると署名が無効になります。", "此文件已签名：拼合会使签名失效。", "此檔案已簽署：平面化會使簽章失效。", "서명된 파일입니다. 병합하면 서명이 무효가 됩니다.", "Le fichier est signé : l’aplatir casserait ses signatures.", "Die Datei ist signiert: Reduzieren würde die Signaturen ungültig machen."],
    "Err_FlattenCannotDraw": ["Washi cannot draw these fields in their fonts: {0}.", "次の記入欄はそのフォントで描けません: {0}", "Washi 无法用其字体绘制这些栏位：{0}。", "Washi 無法用其字型繪製這些欄位：{0}。", "Washi가 해당 글꼴로 그릴 수 없는 입력란: {0}", "Washi ne peut pas dessiner ces champs dans leur police : {0}.", "Washi kann diese Felder nicht in ihrer Schrift zeichnen: {0}."],
    "Err_FlattenChanged": ["Page {0} looks different after flattening ({1} pixels); the copy was not kept.", "確定すると {0} ページ目の見た目が変わるため（{1} ピクセル）、コピーは保存しませんでした。", "拼合后第 {0} 页的外观不同（{1} 像素），未保存副本。", "平面化後第 {0} 頁的外觀不同（{1} 像素），未儲存副本。", "병합 후 {0}쪽의 모양이 달라져({1}픽셀) 사본을 저장하지 않았습니다.", "La page {0} change d’aspect une fois aplatie ({1} pixels) ; la copie n’a pas été conservée.", "Seite {0} sieht nach dem Reduzieren anders aus ({1} Pixel); die Kopie wurde nicht behalten."],
    "Err_FlattenCheck": ["Checking the flattened copy failed ({0}, page {1}); it was not kept.", "確定したコピーの確認に失敗しました（{0}、{1} ページ）。保存しませんでした。", "拼合副本检查失败（{0}，第 {1} 页），未保存。", "平面化副本檢查失敗（{0}，第 {1} 頁），未儲存。", "병합한 사본 확인에 실패했습니다({0}, {1}쪽). 저장하지 않았습니다.", "La vérification de la copie aplatie a échoué ({0}, page {1}) ; elle n’a pas été conservée.", "Die Prüfung der reduzierten Kopie ist fehlgeschlagen ({0}, Seite {1}); sie wurde nicht behalten."],
    "Err_PdfaSigned": ["The file is signed: making it PDF/A would break its signatures.", "このファイルには署名があります。PDF/A にすると署名が無効になります。", "此文件已签名：转为 PDF/A 会使签名失效。", "此檔案已簽署：轉為 PDF/A 會使簽章失效。", "서명된 파일입니다. PDF/A로 만들면 서명이 무효가 됩니다.", "Le fichier est signé : le convertir en PDF/A casserait ses signatures.", "Die Datei ist signiert: PDF/A würde die Signaturen ungültig machen."],
    "Err_PdfaFont": ["These fonts are not embedded and cannot be replaced: {0}.", "埋め込まれておらず、置き換えもできないフォントがあります: {0}", "以下字体未嵌入且无法替换：{0}。", "以下字型未內嵌且無法替換：{0}。", "포함되지 않았고 대체할 수도 없는 글꼴: {0}", "Ces polices ne sont pas incorporées et ne peuvent pas être remplacées : {0}.", "Diese Schriften sind nicht eingebettet und lassen sich nicht ersetzen: {0}."],
    "Err_PdfaCmyk": ["The file paints in CMYK ({0}) and no CMYK colour profile was found on this computer to describe it.", "このファイルは CMYK で色を塗っていますが（{0}）、それを表す CMYK のカラープロファイルがこのコンピューターに見つかりません。", "此文件使用 CMYK 颜色（{0}），但这台电脑上没有可描述它的 CMYK 色彩配置文件。", "此檔案使用 CMYK 色彩（{0}），但這台電腦上沒有可描述它的 CMYK 色彩描述檔。", "이 파일은 CMYK로 색을 칠하는데({0}) 이를 나타낼 CMYK 색상 프로필이 이 컴퓨터에 없습니다.", "Le fichier peint en CMJN ({0}) et aucun profil couleur CMJN n’a été trouvé sur cet ordinateur pour le décrire.", "Die Datei malt in CMYK ({0}), und auf diesem Computer wurde kein CMYK-Farbprofil gefunden, das es beschreibt."],
    "Err_PdfaCannot": ["Washi cannot make this file PDF/A: {0}.", "このファイルは PDF/A にできません: {0}", "Washi 无法将此文件转为 PDF/A：{0}。", "Washi 無法將此檔案轉為 PDF/A：{0}。", "Washi가 이 파일을 PDF/A로 만들 수 없습니다: {0}", "Washi ne peut pas convertir ce fichier en PDF/A : {0}.", "Washi kann diese Datei nicht in PDF/A umwandeln: {0}."],
    "Err_PdfaCheck": ["The PDF/A copy still has: {0}; it was not kept.", "PDF/A のコピーにまだ問題が残っています: {0}。保存しませんでした。", "PDF/A 副本仍有问题：{0}；未保存。", "PDF/A 副本仍有問題：{0}；未儲存。", "PDF/A 사본에 아직 남은 문제: {0}. 저장하지 않았습니다.", "La copie PDF/A a encore : {0} ; elle n’a pas été conservée.", "Die PDF/A-Kopie hat noch: {0}; sie wurde nicht behalten."],
    "Err_PdfaChanged": ["Page {0} looks or reads differently in the PDF/A copy; it was not kept.", "PDF/A のコピーでは {0} ページ目の見た目か文字が変わるため、保存しませんでした。", "PDF/A 副本中第 {0} 页的外观或文字不同，未保存。", "PDF/A 副本中第 {0} 頁的外觀或文字不同，未儲存。", "PDF/A 사본에서 {0}쪽의 모양이나 글자가 달라져 저장하지 않았습니다.", "La page {0} a un autre aspect ou un autre texte dans la copie PDF/A ; elle n’a pas été conservée.", "Seite {0} sieht in der PDF/A-Kopie anders aus oder liest sich anders; sie wurde nicht behalten."],
    "Ui_ToolArchive": ["Finalize", "確定・保存", "定稿", "定稿", "확정", "Finaliser", "Abschließen"],
    "Ui_ArchiveTitle": ["Flatten and archive", "確定と長期保存", "拼合与长期保存", "平面化與長期保存", "병합과 장기 보존", "Aplatir et archiver", "Reduzieren und archivieren"],
    "Ui_ArchiveLead": ["Make a final version to submit or keep: fields and comments become part of the page, or a PDF/A copy that will look the same in years to come.", "提出用・保管用の最終版を作ります。記入欄やコメントをページの一部にしたり、何年たっても同じように表示される PDF/A のコピーを作ったりします。", "制作提交或保存用的最终版本：把表单栏位和注释变成页面的一部分，或生成多年后外观依旧的 PDF/A 副本。", "製作提交或保存用的最終版本：把表單欄位和註解變成頁面的一部分，或產生多年後外觀依舊的 PDF/A 副本。", "제출하거나 보관할 최종본을 만듭니다. 입력란과 주석을 페이지의 일부로 만들거나, 몇 년이 지나도 똑같이 보이는 PDF/A 사본을 만듭니다.", "Créez une version finale à envoyer ou à conserver : les champs et commentaires font partie de la page, ou une copie PDF/A qui gardera son aspect pendant des années.", "Eine endgültige Fassung zum Einreichen oder Aufbewahren: Felder und Kommentare werden Teil der Seite, oder eine PDF/A-Kopie, die auch in Jahren gleich aussieht."],
    "Ui_FlattenTitle": ["Flatten", "確定（フラット化）", "拼合", "平面化", "병합(평면화)", "Aplatir", "Reduzieren"],
    "Ui_FlattenLead": ["Each field and comment is drawn into the page exactly as it shows now and can no longer be changed. Links stay.", "記入欄とコメントを今の見た目のままページに描き込みます。以後は変更できなくなります。リンクは残ります。", "把每个栏位和注释按当前外观画进页面，之后无法再修改。链接保留。", "把每個欄位和註解按目前外觀畫進頁面，之後無法再修改。連結保留。", "각 입력란과 주석을 지금 보이는 그대로 페이지에 그려 넣으며, 이후에는 바꿀 수 없습니다. 링크는 남습니다.", "Chaque champ et commentaire est dessiné dans la page tel qu’il apparaît et ne peut plus être modifié. Les liens restent.", "Jedes Feld und jeder Kommentar wird genau so, wie er jetzt aussieht, in die Seite gezeichnet und lässt sich nicht mehr ändern. Links bleiben."],
    "Ui_FlattenFields": ["Form fields", "記入欄", "表单栏位", "表單欄位", "입력란", "Champs de formulaire", "Formularfelder"],
    "Ui_FlattenComments": ["Comments", "コメント", "注释", "註解", "주석", "Commentaires", "Kommentare"],
    "Ui_FlattenRun": ["Flatten and save copy…", "確定してコピーを保存…", "拼合并另存副本…", "平面化並另存副本…", "병합하여 사본 저장…", "Aplatir et enregistrer une copie…", "Reduzieren und Kopie speichern…"],
    "Ui_Flattening": ["Flattening…", "確定しています…", "正在拼合…", "正在平面化…", "병합하는 중…", "Aplatissement…", "Wird reduziert…"],
    "Ui_Flattened": ["Saved {0}: {1} form field(s) and {2} comment(s) are now part of the page. Checked: every page looks as it did.", "{0} を保存しました：記入欄 {1} 件とコメント {2} 件をページの一部にしました。確認済み：どのページも見た目は変わっていません。", "已保存 {0}：{1} 个表单栏位和 {2} 条注释已成为页面的一部分。已检查：每页外观不变。", "已儲存 {0}：{1} 個表單欄位和 {2} 則註解已成為頁面的一部分。已檢查：每頁外觀不變。", "{0} 저장: 입력란 {1}개와 주석 {2}개가 페이지의 일부가 되었습니다. 확인: 모든 페이지의 모양이 그대로입니다.", "{0} enregistré : {1} champ(s) et {2} commentaire(s) font désormais partie de la page. Vérifié : chaque page a le même aspect.", "{0} gespeichert: {1} Formularfeld(er) und {2} Kommentar(e) sind jetzt Teil der Seite. Geprüft: Jede Seite sieht aus wie zuvor."],
    "Ui_PdfaTitle": ["PDF/A (long-term archive)", "PDF/A（長期保存）", "PDF/A（长期保存）", "PDF/A（長期保存）", "PDF/A(장기 보존)", "PDF/A (archivage)", "PDF/A (Langzeitarchiv)"],
    "Ui_PdfaLead": ["PDF/A-2b keeps everything a page needs inside the file: fonts are embedded (a font that is only named is replaced by one of this computer, each character staying where it was), colours are tied to a profile, and nothing runs. Scripts, attachments and field calculations are taken out.", "PDF/A-2b は、ページの表示に必要なものをすべてファイルの中に持ちます。フォントは埋め込み（名前だけのフォントはこのコンピューターのフォントに置き換え、文字の位置はそのまま）、色はプロファイルに結び付け、何も実行されないようにします。スクリプト・添付ファイル・記入欄の計算は取り除きます。", "PDF/A-2b 把页面所需的一切都放在文件内：嵌入字体（仅有名称的字体用这台电脑的字体替换，每个字符位置不变），颜色绑定色彩配置文件，且不运行任何内容。脚本、附件和栏位计算会被移除。", "PDF/A-2b 把頁面所需的一切都放在檔案內：內嵌字型（只有名稱的字型以這台電腦的字型替換，每個字元位置不變），色彩綁定色彩描述檔，且不執行任何內容。指令碼、附件和欄位計算會被移除。", "PDF/A-2b는 페이지에 필요한 모든 것을 파일 안에 담습니다. 글꼴을 포함하고(이름만 있는 글꼴은 이 컴퓨터의 글꼴로 바꾸며 글자 위치는 그대로), 색상을 프로필에 연결하며, 아무것도 실행되지 않게 합니다. 스크립트·첨부 파일·입력란 계산은 제거됩니다.", "Le PDF/A-2b garde dans le fichier tout ce dont une page a besoin : polices incorporées (une police seulement nommée est remplacée par une police de cet ordinateur, chaque caractère restant à sa place), couleurs liées à un profil, rien qui s’exécute. Scripts, pièces jointes et calculs de champs sont retirés.", "PDF/A-2b enthält alles, was eine Seite braucht, in der Datei: Schriften eingebettet (eine nur genannte Schrift wird durch eine dieses Computers ersetzt, jedes Zeichen bleibt an seiner Stelle), Farben an ein Profil gebunden, nichts wird ausgeführt. Skripte, Anhänge und Feldberechnungen werden entfernt."],
    "Ui_PdfaCheckRun": ["Check", "確認", "检查", "檢查", "확인", "Vérifier", "Prüfen"],
    "Ui_PdfaChecking": ["Checking…", "確認しています…", "正在检查…", "正在檢查…", "확인하는 중…", "Vérification…", "Wird geprüft…"],
    "Ui_PdfaOk": ["This file already meets the PDF/A-2b rules Washi checks.", "このファイルは、Washi が確認する PDF/A-2b の規則をすでに満たしています。", "此文件已符合 Washi 检查的 PDF/A-2b 规则。", "此檔案已符合 Washi 檢查的 PDF/A-2b 規則。", "이 파일은 Washi가 확인하는 PDF/A-2b 규칙을 이미 충족합니다.", "Ce fichier respecte déjà les règles PDF/A-2b que Washi vérifie.", "Diese Datei erfüllt bereits die PDF/A-2b-Regeln, die Washi prüft."],
    "Ui_PdfaIssues": ["Between this file and PDF/A-2b (a PDF/A copy fixes these):", "PDF/A-2b にするために直す点（PDF/A のコピーで直します）:", "转为 PDF/A-2b 需要处理的地方（PDF/A 副本会处理）：", "轉為 PDF/A-2b 需要處理的地方（PDF/A 副本會處理）：", "PDF/A-2b가 되려면 고칠 점(PDF/A 사본에서 고칩니다):", "Ce qui sépare ce fichier du PDF/A-2b (une copie PDF/A le corrige) :", "Was diese Datei von PDF/A-2b trennt (eine PDF/A-Kopie behebt es):"],
    "Ui_PdfaFlattenFirst": ["Flatten fields and comments first", "先に記入欄とコメントを確定する", "先拼合表单栏位和注释", "先平面化表單欄位和註解", "먼저 입력란과 주석을 병합", "Aplatir d’abord les champs et commentaires", "Zuerst Felder und Kommentare reduzieren"],
    "Ui_PdfaRun": ["Save as PDF/A…", "PDF/A で保存…", "另存为 PDF/A…", "另存為 PDF/A…", "PDF/A로 저장…", "Enregistrer en PDF/A…", "Als PDF/A speichern…"],
    "Ui_PdfaConverting": ["Making a PDF/A copy…", "PDF/A のコピーを作っています…", "正在生成 PDF/A 副本…", "正在產生 PDF/A 副本…", "PDF/A 사본을 만드는 중…", "Création de la copie PDF/A…", "PDF/A-Kopie wird erstellt…"],
    "Ui_PdfaSaved": ["Saved {0} as PDF/A-2b. Checked: it meets the rules Washi checks, every page looks as it did and its text reads the same.", "{0} を PDF/A-2b で保存しました。確認済み：Washi が確認する規則を満たし、どのページも見た目と文字が変わっていません。", "已将 {0} 保存为 PDF/A-2b。已检查：符合 Washi 检查的规则，每页外观和文字不变。", "已將 {0} 儲存為 PDF/A-2b。已檢查：符合 Washi 檢查的規則，每頁外觀和文字不變。", "{0}을(를) PDF/A-2b로 저장했습니다. 확인: Washi가 확인하는 규칙을 충족하며 모든 페이지의 모양과 글자가 그대로입니다.", "{0} enregistré en PDF/A-2b. Vérifié : il respecte les règles que Washi vérifie, chaque page a le même aspect et le même texte.", "{0} als PDF/A-2b gespeichert. Geprüft: Es erfüllt die Regeln, die Washi prüft, jede Seite sieht gleich aus und ihr Text liest sich gleich."],
    "Ui_PdfaChanges": ["Changed:", "変更した点:", "更改：", "變更：", "바꾼 점:", "Modifié :", "Geändert:"],
    "Ui_PdfaFontReplaced": ["{0} → {1} (embedded)", "{0} → {1}（埋め込み）", "{0} → {1}（已嵌入）", "{0} → {1}（已內嵌）", "{0} → {1}(포함됨)", "{0} → {1} (incorporée)", "{0} → {1} (eingebettet)"],
    "Pdfa_Version": ["File version newer than PDF 1.7", "PDF 1.7 より新しいファイル形式", "文件版本高于 PDF 1.7", "檔案版本高於 PDF 1.7", "PDF 1.7보다 새로운 파일 형식", "Version de fichier postérieure à PDF 1.7", "Dateiversion neuer als PDF 1.7"],
    "Pdfa_Encrypted": ["Password protection (encryption)", "パスワード保護（暗号化）", "密码保护（加密）", "密碼保護（加密）", "암호 보호(암호화)", "Protection par mot de passe (chiffrement)", "Passwortschutz (Verschlüsselung)"],
    "Pdfa_Metadata": ["XMP metadata that says PDF/A and matches the document properties", "PDF/A であることを示し、文書のプロパティと一致する XMP メタデータ", "声明 PDF/A 且与文档属性一致的 XMP 元数据", "宣告 PDF/A 且與文件屬性一致的 XMP 中繼資料", "PDF/A임을 밝히고 문서 속성과 일치하는 XMP 메타데이터", "Métadonnées XMP déclarant le PDF/A et conformes aux propriétés", "XMP-Metadaten, die PDF/A angeben und zu den Dokumenteigenschaften passen"],
    "Pdfa_OutputIntent": ["Colour profile for the file (output intent)", "ファイルのカラープロファイル（出力インテント）", "文件的色彩配置文件（输出意图）", "檔案的色彩描述檔（輸出意圖）", "파일의 색상 프로필(출력 의도)", "Profil couleur du fichier (intention de sortie)", "Farbprofil der Datei (Output Intent)"],
    "Pdfa_Cmyk": ["Colours in CMYK", "CMYK の色", "CMYK 颜色", "CMYK 色彩", "CMYK 색상", "Couleurs en CMJN", "Farben in CMYK"],
    "Pdfa_Rgb": ["RGB colours tied to sRGB", "RGB の色を sRGB に結び付け", "RGB 颜色绑定 sRGB", "RGB 色彩綁定 sRGB", "RGB 색상을 sRGB에 연결", "Couleurs RVB liées au sRGB", "RGB-Farben an sRGB gebunden"],
    "Pdfa_Fonts": ["Fonts not embedded", "埋め込まれていないフォント", "未嵌入的字体", "未內嵌的字型", "포함되지 않은 글꼴", "Polices non incorporées", "Nicht eingebettete Schriften"],
    "Pdfa_Scripts": ["JavaScript", "JavaScript", "JavaScript", "JavaScript", "JavaScript", "JavaScript", "JavaScript"],
    "Pdfa_Actions": ["Actions that run (on opening, on events, in fields)", "実行される動作（開いたとき・操作時・記入欄）", "会运行的动作（打开时、事件时、栏位中）", "會執行的動作（開啟時、事件時、欄位中）", "실행되는 동작(열 때, 이벤트, 입력란)", "Actions qui s’exécutent (à l’ouverture, sur événement, dans les champs)", "Aktionen, die ausgeführt werden (beim Öffnen, bei Ereignissen, in Feldern)"],
    "Pdfa_Attachments": ["Attached files", "添付ファイル", "附件", "附件", "첨부 파일", "Pièces jointes", "Angehängte Dateien"],
    "Pdfa_Xfa": ["XFA form data", "XFA フォームのデータ", "XFA 表单数据", "XFA 表單資料", "XFA 양식 데이터", "Données de formulaire XFA", "XFA-Formulardaten"],
    "Pdfa_Fields": ["Fields without an appearance of their own", "自分の表示を持たない記入欄", "没有自身外观的栏位", "沒有自身外觀的欄位", "자체 모양이 없는 입력란", "Champs sans apparence propre", "Felder ohne eigenes Erscheinungsbild"],
    "Pdfa_Annotations": ["Hidden comments, sounds, videos and 3D", "非表示のコメント・音声・動画・3D", "隐藏的注释、声音、视频和 3D", "隱藏的註解、聲音、影片和 3D", "숨겨진 주석·소리·동영상·3D", "Commentaires masqués, sons, vidéos et 3D", "Versteckte Kommentare, Töne, Videos und 3D"],
    "Pdfa_AnnotationFlags": ["Comments and fields not set to print", "印刷しない設定のコメント・記入欄", "设为不打印的注释和栏位", "設為不列印的註解和欄位", "인쇄하지 않도록 설정된 주석·입력란", "Commentaires et champs non imprimables", "Kommentare und Felder, die nicht gedruckt werden"],
    "Pdfa_Appearances": ["Extra or missing appearances", "余分な・欠けている表示", "多余或缺少的外观", "多餘或缺少的外觀", "불필요하거나 빠진 모양", "Apparences en trop ou manquantes", "Überzählige oder fehlende Erscheinungsbilder"],
    "Pdfa_Images": ["Image smoothing and alternate images", "画像の補間指定と代替画像", "图像插值和替代图像", "影像內插與替代影像", "이미지 보간과 대체 이미지", "Lissage et images de remplacement", "Bildglättung und Alternativbilder"],
    "Pdfa_External": ["Content outside the file", "ファイル外の内容", "文件外的内容", "檔案外的內容", "파일 밖의 내용", "Contenu hors du fichier", "Inhalt außerhalb der Datei"],
    "Pdfa_Graphics": ["Transfer functions and halftones", "転送関数と網点の指定", "传递函数和半色调", "轉換函數與半色調", "전달 함수와 하프톤", "Fonctions de transfert et trames", "Transferfunktionen und Raster"],
    "Pdfa_Lzw": ["LZW compression", "LZW 圧縮", "LZW 压缩", "LZW 壓縮", "LZW 압축", "Compression LZW", "LZW-Komprimierung"],
    "Pdfa_Layers": ["Layer settings", "レイヤーの設定", "图层设置", "圖層設定", "레이어 설정", "Réglages des calques", "Ebeneneinstellungen"],
    "Pdfa_Other": ["Other entries PDF/A does not allow", "PDF/A で使えないその他の項目", "PDF/A 不允许的其他项目", "PDF/A 不允許的其他項目", "PDF/A에서 허용되지 않는 기타 항목", "Autres entrées interdites en PDF/A", "Sonstige in PDF/A nicht erlaubte Einträge"],
    "Ui_SigLtv": ["long-term validation (LTV)", "長期検証（LTV）対応", "长期验证（LTV）", "長期驗證（LTV）", "장기 검증(LTV)", "validation à long terme (LTV)", "Langzeitvalidierung (LTV)"],
    "Ui_SigDocTimestamp": ["Document time-stamp", "文書タイムスタンプ", "文档时间戳", "文件時間戳記", "문서 타임스탬프", "Horodatage du document", "Dokumentzeitstempel"],
    "Ui_SigCertified": ["Certifies the document: {0}.", "文書を証明する署名: {0}。", "认证文档的签名：{0}。", "認證文件的簽章：{0}。", "문서를 인증하는 서명: {0}.", "Certifie le document : {0}.", "Zertifiziert das Dokument: {0}."],
    "Ui_SigPerm_1": ["no changes allowed", "変更は不可", "不允许任何修改", "不允許任何修改", "변경 불가", "aucune modification permise", "keine Änderungen erlaubt"],
    "Ui_SigPerm_2": ["form filling and signing allowed", "フォーム入力と署名のみ可", "仅允许填写表单和签名", "僅允許填寫表單和簽署", "양식 입력과 서명만 허용", "remplissage et signature permis", "Ausfüllen und Signieren erlaubt"],
    "Ui_SigPerm_3": ["form filling, signing and comments allowed", "フォーム入力・署名・コメントのみ可", "允许填写表单、签名和注释", "允許填寫表單、簽署和註解", "양식 입력·서명·주석 허용", "remplissage, signature et commentaires permis", "Ausfüllen, Signieren und Kommentare erlaubt"],
    "Ui_SigChanges": ["Changed since: {0}.", "署名後の変更: {0}。", "签名后的修改：{0}。", "簽署後的修改：{0}。", "서명 후 변경: {0}.", "Modifié depuis : {0}.", "Seitdem geändert: {0}."],
    "Ui_SigChange_validation": ["validation data", "検証情報", "验证信息", "驗證資訊", "검증 정보", "données de validation", "Validierungsdaten"],
    "Ui_SigChange_timestamp": ["document time-stamp", "文書タイムスタンプ", "文档时间戳", "文件時間戳記", "문서 타임스탬프", "horodatage du document", "Dokumentzeitstempel"],
    "Ui_SigChange_signature": ["signatures", "署名", "签名", "簽章", "서명", "signatures", "Signaturen"],
    "Ui_SigChange_form": ["form filling", "フォーム入力", "表单填写", "表單填寫", "양식 입력", "remplissage du formulaire", "Ausfüllen des Formulars"],
    "Ui_SigChange_metadata": ["document properties", "文書のプロパティ", "文档属性", "文件屬性", "문서 속성", "propriétés du document", "Dokumenteigenschaften"],
    "Ui_SigChange_comments": ["comments", "コメント", "注释", "註解", "주석", "commentaires", "Kommentare"],
    "Ui_SigChange_other": ["other changes (content and more)", "その他の変更（内容など）", "其他修改（内容等）", "其他修改（內容等）", "기타 변경(내용 등)", "autres modifications (contenu, etc.)", "andere Änderungen (Inhalt usw.)"],
    "Ui_SigCertify": ["Certify the document (as its author)", "文書の証明（作成者として）", "认证文档（作为作者）", "認證文件（作為作者）", "문서 인증(작성자로서)", "Certifier le document (en tant qu’auteur)", "Dokument zertifizieren (als Verfasser)"],
    "Ui_SigCertifyNone": ["No: an ordinary signature", "しない（通常の署名）", "不认证（普通签名）", "不認證（一般簽章）", "안 함(일반 서명)", "Non : signature ordinaire", "Nein: gewöhnliche Signatur"],
    "Ui_SigCertifyNote": ["Only the first signature can certify. Readers are told which changes the document still allows; anything else marks the signature as broken.", "証明できるのは最初の署名だけです。閲覧者には、その後に認められる変更が示され、それ以外の変更があると署名が無効と表示されます。", "只有第一个签名可以认证。阅读者会看到文档之后允许的修改，其他修改会使签名显示为无效。", "只有第一個簽章可以認證。閱讀者會看到文件之後允許的修改，其他修改會使簽章顯示為無效。", "첫 서명만 인증할 수 있습니다. 읽는 사람에게 이후 허용되는 변경이 표시되며, 그 밖의 변경은 서명을 무효로 표시합니다.", "Seule la première signature peut certifier. Les lecteurs voient quelles modifications restent permises ; toute autre rend la signature non valide.", "Nur die erste Signatur kann zertifizieren. Leser sehen, welche Änderungen noch erlaubt sind; alles andere macht die Signatur ungültig."],
    "Ui_SigLtvOption": ["Add long-term validation (LTV)", "長期検証（LTV）情報を追加する", "添加长期验证（LTV）信息", "加入長期驗證（LTV）資訊", "장기 검증(LTV) 정보 추가", "Ajouter la validation à long terme (LTV)", "Langzeitvalidierung (LTV) hinzufügen"],
    "Ui_SigLtvNote": ["Fetches the certificates' revocation status from the services they name (only certificate identifiers are sent) and keeps it in the file, so the signature can still be checked offline after the certificates expire.", "証明書に記載された失効確認サービスから状態を取得してファイルに保存します（送るのは証明書の識別情報だけです）。証明書の期限が切れた後も、オフラインで署名を確認できます。", "从证书指定的服务获取吊销状态并保存在文件中（只发送证书的识别信息），证书过期后仍可离线核实签名。", "從憑證指定的服務取得撤銷狀態並儲存在檔案中（只傳送憑證的識別資訊），憑證過期後仍可離線核實簽章。", "인증서에 적힌 서비스에서 폐기 상태를 받아 파일에 저장합니다(인증서 식별 정보만 보냄). 인증서가 만료된 뒤에도 오프라인으로 서명을 확인할 수 있습니다.", "Récupère l’état de révocation des certificats auprès des services qu’ils indiquent (seuls leurs identifiants sont envoyés) et le garde dans le fichier : la signature reste vérifiable hors ligne après l’expiration des certificats.", "Holt den Sperrstatus der Zertifikate bei den darin genannten Diensten (nur Zertifikatskennungen werden gesendet) und speichert ihn in der Datei, damit die Signatur auch nach Ablauf der Zertifikate offline prüfbar bleibt."],
    "Ui_SigAddLtv": ["Add long-term validation…", "長期検証情報を追加…", "添加长期验证信息…", "加入長期驗證資訊…", "장기 검증 정보 추가…", "Ajouter la validation à long terme…", "Langzeitvalidierung hinzufügen…"],
    "Ui_SigAddLtvNote": ["For signatures already in the file. With a time-stamping service below, a document time-stamp is added too; adding one again every few years keeps the file provable.", "ファイル内の既存の署名に対して追加します。下のタイムスタンプサービスを指定すると文書タイムスタンプも付きます。数年ごとに付け直すと、ファイルの証明力が保たれます。", "针对文件中已有的签名。若在下方填写时间戳服务，还会加上文档时间戳；每隔几年重新添加，可保持文件的证明力。", "針對檔案中已有的簽章。若在下方填寫時間戳記服務，還會加上文件時間戳記；每隔幾年重新加入，可保持檔案的證明力。", "파일에 이미 있는 서명에 추가합니다. 아래에 타임스탬프 서비스를 지정하면 문서 타임스탬프도 붙고, 몇 년마다 다시 붙이면 증명력이 유지됩니다.", "Pour les signatures déjà présentes. Avec un service d’horodatage ci-dessous, un horodatage du document est ajouté ; le renouveler tous les quelques ans garde le fichier probant.", "Für Signaturen, die schon in der Datei sind. Mit einem Zeitstempeldienst unten kommt ein Dokumentzeitstempel hinzu; ihn alle paar Jahre zu erneuern, hält die Datei beweiskräftig."],
    "Ui_SigAddingLtv": ["Fetching validation data…", "検証情報を取得しています…", "正在获取验证信息…", "正在取得驗證資訊…", "검증 정보를 받는 중…", "Récupération des données de validation…", "Validierungsdaten werden geholt…"],
    "Ui_SigLtvAdded": ["Saved {0}: {1} certificate(s), {2} OCSP answer(s) and {3} revocation list(s) added.", "{0} を保存しました（証明書 {1} 件、OCSP 応答 {2} 件、失効リスト {3} 件を追加）。", "已保存 {0}（添加了 {1} 个证书、{2} 个 OCSP 响应和 {3} 个吊销列表）。", "已儲存 {0}（加入了 {1} 個憑證、{2} 個 OCSP 回應和 {3} 個撤銷清單）。", "{0} 저장(인증서 {1}개, OCSP 응답 {2}개, 폐기 목록 {3}개 추가).", "{0} enregistré : {1} certificat(s), {2} réponse(s) OCSP et {3} liste(s) de révocation ajoutés.", "{0} gespeichert: {1} Zertifikat(e), {2} OCSP-Antwort(en) und {3} Sperrliste(n) hinzugefügt."],
    "Ui_SigLtvStamped": ["A document time-stamp was added.", "文書タイムスタンプを付けました。", "已添加文档时间戳。", "已加入文件時間戳記。", "문서 타임스탬프를 붙였습니다.", "Un horodatage du document a été ajouté.", "Ein Dokumentzeitstempel wurde hinzugefügt."],
    "Ui_SigLtvDone": ["Long-term validation added.", "長期検証情報を追加しました。", "已添加长期验证信息。", "已加入長期驗證資訊。", "장기 검증 정보를 추가했습니다.", "Validation à long terme ajoutée.", "Langzeitvalidierung hinzugefügt."],
    "Ui_SigLtvMissing": ["Not yet verifiable long-term: {0} (some validation data could not be fetched).", "まだ長期検証できない署名: {0}（取得できなかった検証情報があります）。", "尚不能长期验证：{0}（有些验证信息无法获取）。", "尚不能長期驗證：{0}（有些驗證資訊無法取得）。", "아직 장기 검증할 수 없는 서명: {0}(받지 못한 검증 정보가 있음).", "Pas encore vérifiable à long terme : {0} (certaines données n’ont pas pu être récupérées).", "Noch nicht langfristig prüfbar: {0} (einige Validierungsdaten waren nicht abrufbar)."],
    "Err_CertifyLevel": ["Certification allows 1 (no changes), 2 (form filling and signing) or 3 (also comments).", "証明のレベルは 1（変更不可）、2（フォーム入力と署名）、3（コメントも可）のいずれかです。", "认证级别为 1（不允许修改）、2（填写表单和签名）或 3（还允许注释）。", "認證等級為 1（不允許修改）、2（填寫表單和簽署）或 3（還允許註解）。", "인증 수준은 1(변경 불가), 2(양식 입력과 서명), 3(주석도 허용) 중 하나입니다.", "La certification permet 1 (aucune modification), 2 (remplissage et signature) ou 3 (aussi les commentaires).", "Die Zertifizierung erlaubt 1 (keine Änderungen), 2 (Ausfüllen und Signieren) oder 3 (auch Kommentare)."],
    "Err_SignCertifiedLocked": ["The document is certified with no changes allowed, so it cannot be signed again.", "この文書は「変更不可」で証明されているため、これ以上署名できません。", "此文档已认证为“不允许修改”，不能再签名。", "此文件已認證為「不允許修改」，不能再簽署。", "이 문서는 '변경 불가'로 인증되어 더 이상 서명할 수 없습니다.", "Le document est certifié sans modification permise : il ne peut plus être signé.", "Das Dokument ist ohne erlaubte Änderungen zertifiziert und kann nicht erneut signiert werden."],
    "Err_CertifyNotFirst": ["Only the first signature can certify a document, and this one is already signed.", "文書を証明できるのは最初の署名だけです。この文書はすでに署名されています。", "只有第一个签名可以认证文档，而此文档已有签名。", "只有第一個簽章可以認證文件，而此文件已有簽章。", "문서는 첫 서명만 인증할 수 있는데, 이 문서는 이미 서명되어 있습니다.", "Seule la première signature peut certifier un document, et celui-ci est déjà signé.", "Nur die erste Signatur kann ein Dokument zertifizieren, und dieses ist bereits signiert."],
    "Err_LtvNone": ["The file has no digital signatures.", "このファイルにはデジタル署名がありません。", "此文件没有数字签名。", "此檔案沒有數位簽章。", "이 파일에는 디지털 서명이 없습니다.", "Le fichier n’a pas de signature numérique.", "Die Datei hat keine digitalen Signaturen."],
    "Err_LtvInvalid": ["The signature “{0}” does not hold, so no validation data is added for it.", "署名「{0}」が有効でないため、検証情報は追加しません。", "签名“{0}”无效，因此不为其添加验证信息。", "簽章「{0}」無效，因此不為其加入驗證資訊。", "서명 “{0}” 이(가) 유효하지 않아 검증 정보를 추가하지 않습니다.", "La signature « {0} » ne tient pas : aucune donnée de validation n’est ajoutée.", "Die Signatur „{0}“ hält nicht; es werden keine Validierungsdaten hinzugefügt."],
    "Err_LtvNothing": ["The file already holds all the validation data that could be found.", "取得できる検証情報はすべてファイルに入っています。", "文件中已包含可以获取的全部验证信息。", "檔案中已包含可以取得的全部驗證資訊。", "받을 수 있는 검증 정보가 이미 모두 파일에 있습니다.", "Le fichier contient déjà toutes les données de validation trouvables.", "Die Datei enthält bereits alle auffindbaren Validierungsdaten."],
    "Err_LtvCheck": ["The signatures did not hold after adding validation data; nothing was kept.", "検証情報を追加すると署名が保てなかったため、保存しませんでした。", "添加验证信息后签名无法保持有效，因此没有保存。", "加入驗證資訊後簽章無法保持有效，因此沒有儲存。", "검증 정보를 추가한 뒤 서명이 유지되지 않아 저장하지 않았습니다.", "Les signatures ne tenaient plus après l’ajout ; rien n’a été conservé.", "Die Signaturen hielten nach dem Hinzufügen nicht mehr; nichts wurde behalten."],
    "Err_LabelPage": ["Page labels start on page {0}, but the PDF has {1} pages.", "ページ番号の表示が {0} ページから始まっていますが、この PDF は {1} ページです。", "页码标签从第 {0} 页开始，但该 PDF 只有 {1} 页。", "頁碼標籤從第 {0} 頁開始，但該 PDF 只有 {1} 頁。", "페이지 레이블이 {0}쪽에서 시작하지만 이 PDF는 {1}쪽입니다.", "Les étiquettes commencent page {0}, mais le PDF a {1} pages.", "Die Seitenbeschriftung beginnt auf Seite {0}, das PDF hat aber {1} Seiten."],
    "Err_LabelStyle": ["“{0}” is not a numbering style (1, I, i, A, a, or - for none).", "「{0}」は番号の形式ではありません（1、I、i、A、a、番号なしは -）。", "“{0}”不是编号样式（1、I、i、A、a，无编号用 -）。", "「{0}」不是編號樣式（1、I、i、A、a，無編號用 -）。", "“{0}” 은(는) 번호 형식이 아닙니다(1, I, i, A, a, 번호 없음은 -).", "« {0} » n’est pas un style de numérotation (1, I, i, A, a, ou - pour aucun).", "„{0}“ ist kein Nummernstil (1, I, i, A, a oder - für keinen)."],
    "Err_LabelFirst": ["Page labels count from 1 or more.", "番号は 1 以上から数えます。", "编号从 1 或更大的数开始。", "編號從 1 或更大的數開始。", "번호는 1 이상부터 셉니다.", "La numérotation commence à 1 ou plus.", "Die Zählung beginnt bei 1 oder höher."],
    "Err_LabelTwice": ["Two runs of page labels start on the same page.", "同じページから始まる番号の区切りが 2 つあります。", "有两段页码标签从同一页开始。", "有兩段頁碼標籤從同一頁開始。", "같은 쪽에서 시작하는 레이블 구간이 두 개 있습니다.", "Deux séries d’étiquettes commencent à la même page.", "Zwei Beschriftungsbereiche beginnen auf derselben Seite."],
    "Err_LabelSpec": ["“{0}” is not page:style[:prefix[:first number]] (for example 1:i,5:1).", "「{0}」は ページ:形式[:接頭辞[:開始番号]] の形ではありません（例: 1:i,5:1）。", "“{0}”不是 页:样式[:前缀[:起始数]] 的形式（例如 1:i,5:1）。", "「{0}」不是 頁:樣式[:前綴[:起始數]] 的形式（例如 1:i,5:1）。", "“{0}” 은(는) 쪽:형식[:접두어[:시작 번호]] 형태가 아닙니다(예: 1:i,5:1).", "« {0} » n’est pas page:style[:préfixe[:premier numéro]] (par exemple 1:i,5:1).", "„{0}“ ist nicht Seite:Stil[:Präfix[:erste Nummer]] (zum Beispiel 1:i,5:1)."],
    "Err_LabelCheck": ["The page labels did not read back as written; the copy was not kept.", "ページ番号の表示が書いた通りに読み戻せなかったため、保存しませんでした。", "页码标签未能按写入内容读回，未保存副本。", "頁碼標籤未能按寫入內容讀回，未儲存副本。", "페이지 레이블이 쓴 대로 읽히지 않아 사본을 저장하지 않았습니다.", "Les étiquettes ne se relisent pas comme écrites ; la copie n’a pas été conservée.", "Die Beschriftung ließ sich nicht wie geschrieben zurücklesen; die Kopie wurde nicht behalten."],
    "Err_AttachmentNone": ["There is no attachment “{0}”.", "添付ファイル「{0}」はありません。", "没有附件“{0}”。", "沒有附件「{0}」。", "첨부 파일 “{0}” 이(가) 없습니다.", "Il n’y a pas de pièce jointe « {0} ».", "Es gibt keinen Anhang „{0}“."],
    "Err_AttachmentsNone": ["The file has no attachments.", "このファイルには添付ファイルがありません。", "此文件没有附件。", "此檔案沒有附件。", "이 파일에는 첨부 파일이 없습니다.", "Le fichier n’a pas de pièce jointe.", "Die Datei hat keine Anhänge."],
    "Err_AttachmentUnreadable": ["The attachment “{0}” cannot be read.", "添付ファイル「{0}」を読み取れません。", "无法读取附件“{0}”。", "無法讀取附件「{0}」。", "첨부 파일 “{0}” 을(를) 읽을 수 없습니다.", "La pièce jointe « {0} » est illisible.", "Der Anhang „{0}“ ist nicht lesbar."],
    "Err_AttachmentNothing": ["Nothing to attach or remove.", "添付・削除するファイルがありません。", "没有要添加或删除的附件。", "沒有要加入或刪除的附件。", "첨부하거나 지울 파일이 없습니다.", "Rien à joindre ni à retirer.", "Nichts anzuhängen oder zu entfernen."],
    "Err_AttachmentCheck": ["The attachments did not read back as written; the copy was not kept.", "添付ファイルが書いた通りに読み戻せなかったため、保存しませんでした。", "附件未能按写入内容读回，未保存副本。", "附件未能按寫入內容讀回，未儲存副本。", "첨부 파일이 쓴 대로 읽히지 않아 사본을 저장하지 않았습니다.", "Les pièces jointes ne se relisent pas comme écrites ; la copie n’a pas été conservée.", "Die Anhänge ließen sich nicht wie geschrieben zurücklesen; die Kopie wurde nicht behalten."],
    "Err_LayerNothing": ["Name the layers to show or hide.", "表示・非表示にするレイヤーを指定してください。", "请指定要显示或隐藏的图层。", "請指定要顯示或隱藏的圖層。", "표시하거나 숨길 레이어를 지정하세요.", "Indiquez les calques à afficher ou masquer.", "Geben Sie die Ebenen an, die gezeigt oder ausgeblendet werden sollen."],
    "Err_LayerBoth": ["“{0}” cannot be both shown and hidden.", "「{0}」を表示と非表示の両方にはできません。", "“{0}”不能同时显示和隐藏。", "「{0}」不能同時顯示和隱藏。", "“{0}” 을(를) 표시와 숨김 둘 다로 할 수 없습니다.", "« {0} » ne peut pas être à la fois affiché et masqué.", "„{0}“ kann nicht zugleich gezeigt und ausgeblendet werden."],
    "Err_LayersNone": ["The file has no layers.", "このファイルにはレイヤーがありません。", "此文件没有图层。", "此檔案沒有圖層。", "이 파일에는 레이어가 없습니다.", "Le fichier n’a pas de calques.", "Die Datei hat keine Ebenen."],
    "Err_LayerNone": ["There is no layer “{0}”.", "レイヤー「{0}」はありません。", "没有图层“{0}”。", "沒有圖層「{0}」。", "레이어 “{0}” 이(가) 없습니다.", "Il n’y a pas de calque « {0} ».", "Es gibt keine Ebene „{0}“."],
    "Err_LayerCheck": ["The layers did not read back as set; the copy was not kept.", "レイヤーが設定通りに読み戻せなかったため、保存しませんでした。", "图层未能按设置读回，未保存副本。", "圖層未能按設定讀回，未儲存副本。", "레이어가 설정대로 읽히지 않아 사본을 저장하지 않았습니다.", "Les calques ne se relisent pas comme réglés ; la copie n’a pas été conservée.", "Die Ebenen ließen sich nicht wie eingestellt zurücklesen; die Kopie wurde nicht behalten."],
    "Err_CertNoRecipients": ["Name at least one certificate that may open the file.", "ファイルを開ける証明書を 1 つ以上指定してください。", "请至少指定一个可以打开文件的证书。", "請至少指定一個可以開啟檔案的憑證。", "파일을 열 수 있는 인증서를 하나 이상 지정하세요.", "Indiquez au moins un certificat qui pourra ouvrir le fichier.", "Geben Sie mindestens ein Zertifikat an, das die Datei öffnen darf."],
    "Err_CertNotRsa": ["The certificate of {0} is not an RSA certificate, which certificate security needs.", "{0} の証明書は RSA ではないため、証明書による暗号化には使えません。", "{0} 的证书不是 RSA 证书，无法用于证书加密。", "{0} 的憑證不是 RSA 憑證，無法用於憑證加密。", "{0} 의 인증서는 RSA 가 아니어서 인증서 암호화에 쓸 수 없습니다.", "Le certificat de {0} n’est pas RSA, ce que la sécurité par certificat exige.", "Das Zertifikat von {0} ist kein RSA-Zertifikat, das die Zertifikatsicherheit braucht."],
    "Err_CertAlready": ["{0} is already encrypted for certificates.", "{0} はすでに証明書で暗号化されています。", "{0} 已经用证书加密。", "{0} 已經用憑證加密。", "{0} 은(는) 이미 인증서로 암호화되어 있습니다.", "{0} est déjà chiffré pour des certificats.", "{0} ist bereits für Zertifikate verschlüsselt."],
    "Err_CertSigned": ["The file is signed: encrypting it would break its signatures.", "このファイルには署名があります。暗号化すると署名が無効になります。", "此文件已签名：加密会使签名失效。", "此檔案已簽署：加密會使簽章失效。", "서명된 파일입니다. 암호화하면 서명이 무효가 됩니다.", "Le fichier est signé : le chiffrer casserait ses signatures.", "Die Datei ist signiert: Verschlüsseln würde die Signaturen brechen."],
    "Err_CertNotEncrypted": ["{0} is not encrypted for certificates.", "{0} は証明書で暗号化されていません。", "{0} 没有用证书加密。", "{0} 沒有用憑證加密。", "{0} 은(는) 인증서로 암호화되어 있지 않습니다.", "{0} n’est pas chiffré pour des certificats.", "{0} ist nicht für Zertifikate verschlüsselt."],
    "Err_CertNotRecipient": ["{0} was not encrypted for the certificate of {1}.", "{0} は {1} の証明書あてに暗号化されていません。", "{0} 不是为 {1} 的证书加密的。", "{0} 不是為 {1} 的憑證加密的。", "{0} 은(는) {1} 의 인증서로 암호화되지 않았습니다.", "{0} n’a pas été chiffré pour le certificat de {1}.", "{0} wurde nicht für das Zertifikat von {1} verschlüsselt."],
    "Err_CertUnsupported": ["This certificate encryption is not one Washi opens: only AES-256.", "この方式の証明書暗号化は開けません（AES-256 のみ対応）。", "Washi 无法打开这种证书加密：只支持 AES-256。", "Washi 無法開啟這種憑證加密：只支援 AES-256。", "이 방식의 인증서 암호화는 열 수 없습니다(AES-256 만 지원).", "Ce chiffrement par certificat n’est pas pris en charge : AES-256 seulement.", "Diese Zertifikatsverschlüsselung öffnet Washi nicht: nur AES-256."],
    "Err_CertCheck": ["The encrypted copy did not read back as the original; it was not kept.", "暗号化したコピーが元と同じに読み戻せなかったため、保存しませんでした。", "加密副本未能读回为原样，未保存。", "加密副本未能讀回為原樣，未儲存。", "암호화한 사본이 원본과 같게 읽히지 않아 저장하지 않았습니다.", "La copie chiffrée ne se relit pas comme l’original ; elle n’a pas été conservée.", "Die verschlüsselte Kopie ließ sich nicht wie das Original zurücklesen; sie wurde nicht behalten."],
    "Ui_ToolDocument": ["Document", "文書", "文档", "文件", "문서", "Document", "Dokument"],
    "Ui_DocTitle": ["Document", "文書の設定", "文档设置", "文件設定", "문서 설정", "Document", "Dokument"],
    "Ui_DocLead": ["Page numbers as the viewer shows them, attached files and layers. Each change saves a copy.", "ビューアに表示するページ番号、添付ファイル、レイヤーを扱います。変更するたびにコピーを保存します。", "查看器显示的页码、附件和图层。每次修改都会另存副本。", "檢視器顯示的頁碼、附件和圖層。每次修改都會另存副本。", "뷰어에 표시되는 페이지 번호, 첨부 파일, 레이어. 바꿀 때마다 사본을 저장합니다.", "Numéros de page affichés, fichiers joints et calques. Chaque changement enregistre une copie.", "Angezeigte Seitenzahlen, Anhänge und Ebenen. Jede Änderung speichert eine Kopie."],
    "Ui_LabelsTitle": ["Page labels", "ページ番号の表示", "页码标签", "頁碼標籤", "페이지 레이블", "Étiquettes de page", "Seitenbeschriftung"],
    "Ui_LabelsLead": ["page:style[:prefix[:first]], comma-separated. Styles: 1, I, i, A, a, or - for none. 1:i,5:1 numbers four pages i–iv, then 1, 2, 3 …", "ページ:形式[:接頭辞[:開始番号]] をカンマで区切って書きます。形式は 1、I、i、A、a、番号なしは -。1:i,5:1 なら最初の 4 ページが i〜iv、その後 1、2、3…", "页:样式[:前缀[:起始数]]，用逗号分隔。样式：1、I、i、A、a，无编号用 -。1:i,5:1 表示前 4 页为 i–iv，之后为 1、2、3…", "頁:樣式[:前綴[:起始數]]，以逗號分隔。樣式：1、I、i、A、a，無編號用 -。1:i,5:1 表示前 4 頁為 i–iv，之後為 1、2、3…", "쪽:형식[:접두어[:시작 번호]] 를 쉼표로 구분합니다. 형식: 1, I, i, A, a, 번호 없음은 -. 1:i,5:1 은 처음 4쪽이 i–iv, 그다음 1, 2, 3…", "page:style[:préfixe[:premier]], séparés par des virgules. Styles : 1, I, i, A, a, ou - pour aucun. 1:i,5:1 numérote quatre pages i–iv, puis 1, 2, 3…", "Seite:Stil[:Präfix[:erste]], durch Kommas getrennt. Stile: 1, I, i, A, a oder - für keinen. 1:i,5:1 nummeriert vier Seiten i–iv, dann 1, 2, 3 …"],
    "Ui_LabelsNone": ["No labels: pages show their plain numbers.", "表示の設定なし: ページはそのままの番号で表示されます。", "没有标签：页面显示普通页码。", "沒有標籤：頁面顯示一般頁碼。", "레이블 없음: 쪽 번호 그대로 표시됩니다.", "Pas d’étiquettes : les pages montrent leur numéro.", "Keine Beschriftung: Seiten zeigen ihre Nummer."],
    "Ui_LabelsRun": ["Save copy with these labels…", "この表示でコピーを保存…", "按这些标签另存副本…", "依這些標籤另存副本…", "이 레이블로 사본 저장…", "Enregistrer une copie avec ces étiquettes…", "Kopie mit dieser Beschriftung speichern…"],
    "Ui_LabelsSaved": ["Saved {0} with {1} run(s) of page labels.", "{0} を保存しました（番号の区切り {1} 件）。", "已保存 {0}（{1} 段页码标签）。", "已儲存 {0}（{1} 段頁碼標籤）。", "{0} 저장 (레이블 구간 {1}개).", "{0} enregistré avec {1} série(s) d’étiquettes.", "{0} mit {1} Beschriftungsbereich(en) gespeichert."],
    "Ui_LabelsRemoved": ["Saved {0} without page labels.", "{0} をページ番号の表示なしで保存しました。", "已保存 {0}（无页码标签）。", "已儲存 {0}（無頁碼標籤）。", "{0} 을(를) 레이블 없이 저장했습니다.", "{0} enregistré sans étiquettes.", "{0} ohne Seitenbeschriftung gespeichert."],
    "Ui_AttachTitle": ["Attachments", "添付ファイル", "附件", "附件", "첨부 파일", "Pièces jointes", "Anhänge"],
    "Ui_AttachNone": ["No attachments.", "添付ファイルはありません。", "没有附件。", "沒有附件。", "첨부 파일이 없습니다.", "Aucune pièce jointe.", "Keine Anhänge."],
    "Ui_AttachOnPage": ["on page {0}", "{0} ページのコメント", "第 {0} 页的注释", "第 {0} 頁的註解", "{0}쪽 주석", "page {0}", "auf Seite {0}"],
    "Ui_AttachSave": ["Save to folder…", "フォルダに保存…", "保存到文件夹…", "儲存到資料夾…", "폴더에 저장…", "Enregistrer dans un dossier…", "In Ordner speichern…"],
    "Ui_AttachRemove": ["Remove ticked", "チェックしたものを削除", "删除勾选的", "刪除勾選的", "선택한 항목 삭제", "Retirer les cochées", "Markierte entfernen"],
    "Ui_AttachDescription": ["Description of new attachments (optional)", "追加するファイルの説明（任意）", "新附件的说明（可选）", "新附件的說明（選填）", "새 첨부 파일 설명 (선택)", "Description des nouvelles pièces jointes (facultatif)", "Beschreibung neuer Anhänge (optional)"],
    "Ui_AttachRun": ["Attach files…", "ファイルを添付…", "添加附件…", "加入附件…", "파일 첨부…", "Joindre des fichiers…", "Dateien anhängen…"],
    "Ui_Attached": ["Saved {0} with {1} file(s) attached.", "{0} を保存しました（{1} 件のファイルを添付）。", "已保存 {0}（添加了 {1} 个附件）。", "已儲存 {0}（加入了 {1} 個附件）。", "{0} 저장 (파일 {1}개 첨부).", "{0} enregistré avec {1} fichier(s) joint(s).", "{0} mit {1} angehängten Datei(en) gespeichert."],
    "Ui_Detached": ["Saved {0} with {1} attachment(s) removed.", "{0} を保存しました（添付ファイル {1} 件を削除）。", "已保存 {0}（删除了 {1} 个附件）。", "已儲存 {0}（刪除了 {1} 個附件）。", "{0} 저장 (첨부 파일 {1}개 삭제).", "{0} enregistré, {1} pièce(s) jointe(s) retirée(s).", "{0} gespeichert, {1} Anhang/Anhänge entfernt."],
    "Ui_AttachmentsSaved": ["Saved {0} file(s) in {1}.", "{1} に {0} 件のファイルを保存しました。", "已在 {1} 保存 {0} 个文件。", "已在 {1} 儲存 {0} 個檔案。", "{1} 에 파일 {0}개를 저장했습니다.", "{0} fichier(s) enregistré(s) dans {1}.", "{0} Datei(en) in {1} gespeichert."],
    "Ui_LayersTitle": ["Layers", "レイヤー", "图层", "圖層", "레이어", "Calques", "Ebenen"],
    "Ui_LayersNone": ["No layers.", "レイヤーはありません。", "没有图层。", "沒有圖層。", "레이어가 없습니다.", "Aucun calque.", "Keine Ebenen."],
    "Ui_LayersLead": ["Ticked layers show (and print) when the file opens.", "チェックしたレイヤーが、ファイルを開いたときに表示・印刷されます。", "勾选的图层在打开文件时显示（并打印）。", "勾選的圖層在開啟檔案時顯示（並列印）。", "선택한 레이어가 파일을 열 때 표시(및 인쇄)됩니다.", "Les calques cochés s’affichent (et s’impriment) à l’ouverture.", "Markierte Ebenen werden beim Öffnen gezeigt (und gedruckt)."],
    "Ui_LayersRun": ["Save copy with these layers…", "このレイヤー設定でコピーを保存…", "按此图层设置另存副本…", "依此圖層設定另存副本…", "이 레이어 설정으로 사본 저장…", "Enregistrer une copie avec ces calques…", "Kopie mit diesen Ebenen speichern…"],
    "Ui_LayersSaved": ["Saved {0} with the layers set.", "{0} をレイヤー設定を変えて保存しました。", "已保存 {0}（已设置图层）。", "已儲存 {0}（已設定圖層）。", "{0} 을(를) 레이어 설정으로 저장했습니다.", "{0} enregistré avec les calques réglés.", "{0} mit eingestellten Ebenen gespeichert."],
    "Ui_LayerPages": ["page {0}", "{0} ページ", "第 {0} 页", "第 {0} 頁", "{0}쪽", "page {0}", "Seite {0}"],
    "Ui_CertFiles": ["Certificates", "証明書", "证书", "憑證", "인증서", "Certificats", "Zertifikate"],
    "Ui_CertLocked": ["{0} is encrypted for certificates: it opens with the digital ID of a recipient.", "{0} は証明書で暗号化されています。受信者のデジタル ID で開けます。", "{0} 已用证书加密：可用收件人的数字标识打开。", "{0} 已用憑證加密：可用收件人的數位識別碼開啟。", "{0} 은(는) 인증서로 암호화되어 있습니다. 수신자의 디지털 ID 로 열 수 있습니다.", "{0} est chiffré pour des certificats : il s’ouvre avec l’identité numérique d’un destinataire.", "{0} ist für Zertifikate verschlüsselt: es öffnet sich mit der digitalen ID eines Empfängers."],
    "Ui_CertRecipient": ["For a certificate from {0} (serial {1})", "{0} が発行した証明書あて（シリアル {1}）", "发给 {0} 签发的证书（序列号 {1}）", "發給 {0} 簽發的憑證（序號 {1}）", "{0} 발급 인증서용(일련번호 {1})", "Pour un certificat de {0} (série {1})", "Für ein Zertifikat von {0} (Seriennummer {1})"],
    "Ui_CertChooseId": ["Choose the digital ID to open it with.", "開くためのデジタル ID を選んでください。", "请选择用来打开的数字标识。", "請選擇用來開啟的數位識別碼。", "열 때 쓸 디지털 ID 를 선택하세요.", "Choisissez l’identité numérique pour l’ouvrir.", "Wählen Sie die digitale ID zum Öffnen."],
    "Ui_CertOpenRun": ["Open with this digital ID…", "このデジタル ID で開く…", "用此数字标识打开…", "用此數位識別碼開啟…", "이 디지털 ID 로 열기…", "Ouvrir avec cette identité…", "Mit dieser ID öffnen…"],
    "Ui_CertOpenNote": ["Saves a copy without the protection, and opens it.", "保護を外したコピーを保存して開きます。", "另存一份去掉保护的副本并打开。", "另存一份去掉保護的副本並開啟。", "보호를 없앤 사본을 저장하고 엽니다.", "Enregistre une copie sans la protection, et l’ouvre.", "Speichert eine Kopie ohne Schutz und öffnet sie."],
    "Ui_CertOpened": ["Saved {0}, opened with the digital ID of {1}.", "{0} を保存しました（{1} のデジタル ID で開きました）。", "已保存 {0}（用 {1} 的数字标识打开）。", "已儲存 {0}（用 {1} 的數位識別碼開啟）。", "{0} 저장 ({1} 의 디지털 ID 로 열었습니다).", "{0} enregistré, ouvert avec l’identité de {1}.", "{0} gespeichert, mit der digitalen ID von {1} geöffnet."],
    "Ui_CertTitle": ["Encrypt for certificates", "証明書で暗号化", "用证书加密", "用憑證加密", "인증서로 암호화", "Chiffrer pour des certificats", "Für Zertifikate verschlüsseln"],
    "Ui_CertLead": ["Only the people whose certificates you add can open the copy, with their digital ID: no password to share. AES-256, as Acrobat's certificate security.", "追加した証明書の持ち主だけが、自分のデジタル ID でコピーを開けます。パスワードを共有する必要はありません。Acrobat の証明書セキュリティと同じ AES-256 です。", "只有你添加的证书的持有人才能用自己的数字标识打开副本，无需共享密码。采用与 Acrobat 证书安全相同的 AES-256。", "只有你加入的憑證的持有人才能用自己的數位識別碼開啟副本，無需分享密碼。採用與 Acrobat 憑證安全相同的 AES-256。", "추가한 인증서의 소유자만 자신의 디지털 ID 로 사본을 열 수 있습니다. 암호를 공유할 필요가 없습니다. Acrobat 인증서 보안과 같은 AES-256.", "Seules les personnes dont vous ajoutez le certificat ouvrent la copie, avec leur identité numérique : aucun mot de passe à partager. AES-256, comme la sécurité par certificat d’Acrobat.", "Nur die Personen, deren Zertifikate Sie hinzufügen, öffnen die Kopie mit ihrer digitalen ID: kein Passwort zum Weitergeben. AES-256 wie Acrobats Zertifikatsicherheit."],
    "Ui_CertAdd": ["Add certificate…", "証明書を追加…", "添加证书…", "加入憑證…", "인증서 추가…", "Ajouter un certificat…", "Zertifikat hinzufügen…"],
    "Ui_CertIncludeMe": ["Let me open it too, with this digital ID:", "自分も開けるようにする（このデジタル ID）:", "我自己也可以打开（用此数字标识）：", "我自己也可以開啟（用此數位識別碼）：", "나도 열 수 있게(이 디지털 ID):", "Pouvoir l’ouvrir moi aussi, avec cette identité :", "Auch ich darf öffnen, mit dieser ID:"],
    "Ui_CertRun": ["Encrypt and save copy…", "暗号化してコピーを保存…", "加密并另存副本…", "加密並另存副本…", "암호화하여 사본 저장…", "Chiffrer et enregistrer une copie…", "Verschlüsseln und Kopie speichern…"],
    "Ui_CertSealed": ["Saved {0}, encrypted for {1} certificate(s).", "{0} を保存しました（証明書 {1} 件あてに暗号化）。", "已保存 {0}（为 {1} 个证书加密）。", "已儲存 {0}（為 {1} 個憑證加密）。", "{0} 저장 (인증서 {1}개용으로 암호화).", "{0} enregistré, chiffré pour {1} certificat(s).", "{0} gespeichert, für {1} Zertifikat(e) verschlüsselt."],
    "Ui_CertUnreadable": ["{0} is not a certificate Washi can read.", "{0} は読み取れる証明書ではありません。", "{0} 不是 Washi 能读取的证书。", "{0} 不是 Washi 能讀取的憑證。", "{0} 은(는) 읽을 수 있는 인증서가 아닙니다.", "{0} n’est pas un certificat lisible.", "{0} ist kein lesbares Zertifikat."],
    "Err_FormDataKind": ["“{0}” is not a form data file (.fdf, .xfdf, .csv or .json).", "「{0}」はフォームデータのファイルではありません（.fdf、.xfdf、.csv、.json）。", "“{0}”不是表单数据文件（.fdf、.xfdf、.csv 或 .json）。", "「{0}」不是表單資料檔（.fdf、.xfdf、.csv 或 .json）。", "“{0}” 은(는) 양식 데이터 파일이 아닙니다(.fdf, .xfdf, .csv, .json).", "« {0} » n’est pas un fichier de données de formulaire (.fdf, .xfdf, .csv ou .json).", "„{0}“ ist keine Formulardaten-Datei (.fdf, .xfdf, .csv oder .json)."],
    "Err_FormDataNoFields": ["{0} has no form fields.", "{0} には記入欄がありません。", "{0} 没有表单栏位。", "{0} 沒有表單欄位。", "{0} 에는 입력란이 없습니다.", "{0} n’a pas de champ de formulaire.", "{0} hat keine Formularfelder."],
    "Err_FormDataCheck": ["The form data did not read back as written; it was not kept.", "フォームデータが書いた通りに読み戻せなかったため、保存しませんでした。", "表单数据未能按写入内容读回，未保存。", "表單資料未能按寫入內容讀回，未儲存。", "양식 데이터가 쓴 대로 읽히지 않아 저장하지 않았습니다.", "Les données ne se relisent pas comme écrites ; le fichier n’a pas été conservé.", "Die Formulardaten ließen sich nicht wie geschrieben zurücklesen; die Datei wurde nicht behalten."],
    "Err_FormDataEmpty": ["{0} has no row of values under its header.", "{0} には見出し行の下に値の行がありません。", "{0} 的标题行下没有数据行。", "{0} 的標題列下沒有資料列。", "{0} 에는 머리글 아래 값 행이 없습니다.", "{0} n’a aucune ligne de valeurs sous son en-tête.", "{0} hat unter der Kopfzeile keine Wertezeile."],
    "Err_FormDataUnreadable": ["{0} could not be read as form data.", "{0} をフォームデータとして読み取れません。", "无法将 {0} 读取为表单数据。", "無法將 {0} 讀取為表單資料。", "{0} 을(를) 양식 데이터로 읽을 수 없습니다.", "{0} n’a pas pu être lu comme données de formulaire.", "{0} ließ sich nicht als Formulardaten lesen."],
    "Err_FormDataNoMatch": ["None of the names in {0} is a field of {1}.", "{0} の名前はどれも {1} の記入欄と一致しません。", "{0} 中的名称都不是 {1} 的栏位。", "{0} 中的名稱都不是 {1} 的欄位。", "{0} 의 이름 중 {1} 의 입력란과 맞는 것이 없습니다.", "Aucun nom de {0} n’est un champ de {1}.", "Keiner der Namen in {0} ist ein Feld von {1}."],
    "Err_FormDataNoColumn": ["{0} has no column “{1}”.", "{0} には「{1}」の列がありません。", "{0} 没有“{1}”列。", "{0} 沒有「{1}」欄。", "{0} 에는 “{1}” 열이 없습니다.", "{0} n’a pas de colonne « {1} ».", "{0} hat keine Spalte „{1}“."],
    "Err_FormDataNothing": ["Give the filled forms to collect.", "集計する記入済みフォームを指定してください。", "请指定要汇总的已填表单。", "請指定要彙總的已填表單。", "모을 작성된 양식을 지정하세요.", "Indiquez les formulaires remplis à rassembler.", "Geben Sie die ausgefüllten Formulare zum Sammeln an."],
    "Ui_FormDataFiles": ["Form data", "フォームデータ", "表单数据", "表單資料", "양식 데이터", "Données de formulaire", "Formulardaten"],
    "Ui_FormDataTitle": ["Form data", "フォームデータ", "表单数据", "表單資料", "양식 데이터", "Données de formulaire", "Formulardaten"],
    "Ui_FormDataLead": ["Save the values typed into the form (XFDF, FDF, CSV or JSON: the extension you give chooses), or fill the form from such a file.", "入力済みの値をファイルに保存したり（XFDF、FDF、CSV、JSON。拡張子で形式が決まります）、そのファイルからフォームに入力したりします。", "把表单中的值保存到文件（XFDF、FDF、CSV 或 JSON，由扩展名决定），或从这样的文件填写表单。", "把表單中的值儲存到檔案（XFDF、FDF、CSV 或 JSON，由副檔名決定），或從這樣的檔案填寫表單。", "양식에 입력된 값을 파일로 저장하거나(XFDF, FDF, CSV, JSON: 확장자로 형식 결정), 그런 파일로 양식을 채웁니다.", "Enregistrez les valeurs saisies (XFDF, FDF, CSV ou JSON : l’extension choisit), ou remplissez le formulaire depuis un tel fichier.", "Die eingetragenen Werte speichern (XFDF, FDF, CSV oder JSON: die Endung entscheidet) oder das Formular aus einer solchen Datei füllen."],
    "Ui_FormDataExport": ["Save data…", "データを保存…", "保存数据…", "儲存資料…", "데이터 저장…", "Enregistrer les données…", "Daten speichern…"],
    "Ui_FormDataImport": ["Fill from data…", "データから入力…", "从数据填写…", "從資料填寫…", "데이터로 채우기…", "Remplir depuis des données…", "Aus Daten füllen…"],
    "Ui_FormDataRowsLead": ["From a table (CSV whose header names the fields), one filled copy per row; or many filled forms into one table.", "表（見出しが記入欄の名前の CSV）から 1 行ごとに記入済みのコピーを作ります。複数の記入済みフォームを 1 つの表にまとめることもできます。", "从表格（标题为栏位名称的 CSV）为每一行生成一份已填副本；也可把多份已填表单汇总为一个表格。", "從表格（標題為欄位名稱的 CSV）為每一列產生一份已填副本；也可把多份已填表單彙總為一個表格。", "표(머리글이 입력란 이름인 CSV)의 행마다 채운 사본을 만들거나, 여러 작성된 양식을 하나의 표로 모읍니다.", "Depuis un tableau (CSV dont l’en-tête nomme les champs), une copie remplie par ligne ; ou plusieurs formulaires remplis en un tableau.", "Aus einer Tabelle (CSV, deren Kopfzeile die Felder nennt) eine ausgefüllte Kopie je Zeile; oder viele ausgefüllte Formulare in eine Tabelle."],
    "Ui_FormDataNameColumn": ["Column that names each copy (optional)", "コピーの名前にする列（任意）", "用作副本名称的列（可选）", "用作副本名稱的欄（選填）", "사본 이름으로 쓸 열 (선택)", "Colonne qui nomme chaque copie (facultatif)", "Spalte, die jede Kopie benennt (optional)"],
    "Ui_FormDataNameColumnHint": ["for example 社員番号", "例: 社員番号", "例如 员工编号", "例如 員工編號", "예: 사원번호", "par exemple Matricule", "zum Beispiel Personalnummer"],
    "Ui_FormDataRows": ["Fill one copy per row…", "1 行ごとにコピーを作成…", "每行生成一份副本…", "每列產生一份副本…", "행마다 사본 만들기…", "Une copie par ligne…", "Eine Kopie je Zeile…"],
    "Ui_FormDataCollect": ["Collect forms into a table…", "フォームを表に集計…", "将表单汇总为表格…", "將表單彙總為表格…", "양식을 표로 모으기…", "Rassembler des formulaires en tableau…", "Formulare in Tabelle sammeln…"],
    "Ui_FormDataCollectName": ["collected", "集計", "汇总", "彙總", "모음", "rassemblement", "gesammelt"],
    "Ui_FormDataExported": ["Saved {0} with {1} field value(s).", "{0} を保存しました（記入欄 {1} 件）。", "已保存 {0}（{1} 个栏位值）。", "已儲存 {0}（{1} 個欄位值）。", "{0} 저장 (입력란 값 {1}개).", "{0} enregistré avec {1} valeur(s).", "{0} mit {1} Feldwert(en) gespeichert."],
    "Ui_FormDataImported": ["Saved {0} with {1} field(s) filled from the data.", "{0} を保存しました（データから {1} 件を入力）。", "已保存 {0}（从数据填写了 {1} 个栏位）。", "已儲存 {0}（從資料填寫了 {1} 個欄位）。", "{0} 저장 (데이터로 {1}개 입력).", "{0} enregistré, {1} champ(s) remplis depuis les données.", "{0} gespeichert, {1} Feld(er) aus den Daten gefüllt."],
    "Ui_FormDataUnknown": ["“{0}” is no field of this form.", "「{0}」はこのフォームの記入欄ではありません。", "“{0}”不是此表单的栏位。", "「{0}」不是此表單的欄位。", "“{0}” 은(는) 이 양식의 입력란이 아닙니다.", "« {0} » n’est pas un champ de ce formulaire.", "„{0}“ ist kein Feld dieses Formulars."],
    "Ui_FormDataFilledMany": ["Filled {0} copies in {1}.", "{1} に {0} 部のコピーを作成しました。", "已在 {1} 生成 {0} 份副本。", "已在 {1} 產生 {0} 份副本。", "{1} 에 사본 {0}개를 만들었습니다.", "{0} copie(s) remplie(s) dans {1}.", "{0} Kopie(n) in {1} ausgefüllt."],
    "Ui_FormDataCollected": ["Saved {0} with {1} form(s).", "{0} を保存しました（フォーム {1} 件）。", "已保存 {0}（{1} 份表单）。", "已儲存 {0}（{1} 份表單）。", "{0} 저장 (양식 {1}개).", "{0} enregistré avec {1} formulaire(s).", "{0} mit {1} Formular(en) gespeichert."],
    "Err_FormLocked": ["{0} does not allow filling in its form.", "{0} はフォームへの入力を許可していません。", "{0} 不允许填写表单。", "{0} 不允許填寫表單。", "{0} 은(는) 양식 입력을 허용하지 않습니다.", "{0} n’autorise pas à remplir son formulaire.", "{0} erlaubt das Ausfüllen des Formulars nicht."],
    "Err_FormFillCheck": ["The filled copy did not read back as filled; it was not kept.", "入力したコピーが入力した通りに読み戻せなかったため、保存しませんでした。", "填写后的副本未能按填写内容读回，未保存。", "填寫後的副本未能按填寫內容讀回，未儲存。", "채운 사본이 채운 대로 읽히지 않아 저장하지 않았습니다.", "La copie remplie ne se relit pas comme remplie ; elle n’a pas été conservée.", "Die ausgefüllte Kopie ließ sich nicht wie ausgefüllt zurücklesen; sie wurde nicht behalten."],
    "Err_FormDataNothingFilled": ["Nothing in the form changed.", "フォームは何も変わりませんでした。", "表单没有任何变化。", "表單沒有任何變化。", "양식이 바뀌지 않았습니다.", "Rien n’a changé dans le formulaire.", "Im Formular hat sich nichts geändert."],
    "Ui_SaveFilled": ["Save filled copy…", "入力したコピーを保存…", "保存已填写的副本…", "儲存已填寫的副本…", "채운 사본 저장…", "Enregistrer la copie remplie…", "Ausgefüllte Kopie speichern…"],
    "Ui_FormFilling": ["This PDF is a form with {0} field(s): click a field to fill it in.", "この PDF は記入欄 {0} 件のフォームです。記入欄をクリックして入力してください。", "此 PDF 是含 {0} 个栏位的表单：点击栏位即可填写。", "此 PDF 是含 {0} 個欄位的表單：按一下欄位即可填寫。", "이 PDF는 입력란 {0}개가 있는 양식입니다. 입력란을 클릭해 채우세요.", "Ce PDF est un formulaire de {0} champ(s) : cliquez sur un champ pour le remplir.", "Dieses PDF ist ein Formular mit {0} Feld(ern): zum Ausfüllen auf ein Feld klicken."],
    "Ui_NothingChanged": ["Nothing was changed in the form.", "フォームは何も変更されていません。", "表单没有任何修改。", "表單沒有任何修改。", "양식에서 바뀐 것이 없습니다.", "Rien n’a été modifié dans le formulaire.", "Im Formular wurde nichts geändert."],
    "Ui_FilledSaved": ["Saved {0} with {1} field(s) filled in.", "{0} を保存しました（{1} 件を入力）。", "已保存 {0}（填写了 {1} 个栏位）。", "已儲存 {0}（填寫了 {1} 個欄位）。", "{0} 저장 (입력란 {1}개 채움).", "{0} enregistré, {1} champ(s) remplis.", "{0} gespeichert, {1} Feld(er) ausgefüllt."],
    "Ui_FieldReadOnly": ["This field is read-only in the form.", "この記入欄はフォームで読み取り専用になっています。", "此栏位在表单中为只读。", "此欄位在表單中為唯讀。", "이 입력란은 양식에서 읽기 전용입니다.", "Ce champ est en lecture seule dans le formulaire.", "Dieses Feld ist im Formular schreibgeschützt."],
    "Ui_FillHint": ["Click a field to type into it, a box to tick it. Save filled copy (⌘S) writes what you changed.", "記入欄をクリックして入力、チェック欄はクリックで切り替えます。「入力したコピーを保存」（⌘S）で変更を書き込みます。", "点击栏位输入，点击复选框勾选。“保存已填写的副本”（⌘S）会写入修改内容。", "按一下欄位輸入，按一下核取方塊勾選。「儲存已填寫的副本」（⌘S）會寫入修改內容。", "입력란을 클릭해 입력하고 체크 상자는 클릭해 선택합니다. ‘채운 사본 저장’(⌘S)이 바꾼 내용을 씁니다.", "Cliquez sur un champ pour y écrire, sur une case pour la cocher. « Enregistrer la copie remplie » (⌘S) écrit vos changements.", "Auf ein Feld klicken, um zu schreiben, auf ein Kästchen, um es anzukreuzen. „Ausgefüllte Kopie speichern“ (⌘S) schreibt die Änderungen."],
    "Ui_FillTitle": ["Fill in the form", "フォームに入力", "填写表单", "填寫表單", "양식 채우기", "Remplir le formulaire", "Formular ausfüllen"],
    "Ui_FillLead": ["This PDF already has fields. Click one to type into it; the list on the right shows what is filled in. Values are checked against the form's own rules, and totals are worked out as you type. Only what you change is written, into a copy; a form protected against changes but open to filling in keeps its protection.", "この PDF にはすでに記入欄があります。クリックして入力してください。右の一覧に入力済みの内容が出ます。値はフォーム自身の規則で確認され、合計は入力に合わせて計算されます。変更した部分だけをコピーに書き込みます。変更は禁止でも入力は許可されているフォームは、保護をそのまま保ちます。", "此 PDF 已有栏位。点击即可输入，右侧列表显示已填写的内容。数值会按表单自身的规则检查，合计会随输入计算。只有修改的部分会写入副本；禁止修改但允许填写的表单会保留其保护。", "此 PDF 已有欄位。按一下即可輸入，右側清單顯示已填寫的內容。數值會依表單自身的規則檢查，合計會隨輸入計算。只有修改的部分會寫入副本；禁止修改但允許填寫的表單會保留其保護。", "이 PDF에는 이미 입력란이 있습니다. 클릭해 입력하세요. 오른쪽 목록에 채운 내용이 보입니다. 값은 양식 자체의 규칙으로 확인되고 합계는 입력하면서 계산됩니다. 바꾼 것만 사본에 씁니다. 변경은 막혀 있지만 입력은 허용된 양식은 보호가 그대로 유지됩니다.", "Ce PDF a déjà des champs. Cliquez pour y écrire ; la liste à droite montre ce qui est rempli. Les valeurs sont vérifiées selon les règles du formulaire, et les totaux calculés au fil de la saisie. Seul ce que vous changez est écrit, dans une copie ; un formulaire protégé contre les modifications mais ouvert au remplissage garde sa protection.", "Dieses PDF hat bereits Felder. Zum Schreiben anklicken; die Liste rechts zeigt, was ausgefüllt ist. Werte werden nach den Regeln des Formulars geprüft, Summen beim Tippen berechnet. Nur Geändertes wird in eine Kopie geschrieben; ein gegen Änderungen geschütztes, aber ausfüllbares Formular behält seinen Schutz."],
    "Ui_FillNewFormTitle": ["A new form instead", "新しいフォームを作る場合", "改为制作新表单", "改為製作新表單", "새 양식을 만들려면", "Un nouveau formulaire à la place", "Stattdessen ein neues Formular"],
    "Ui_FillNewFormNote": ["To make your own fields on this page instead, find the boxes on it. The fields you make are added to the ones it already has.", "このページに自分で記入欄を作る場合は、枠を検出してください。作った記入欄は、今ある記入欄に追加されます。", "如果要在此页面上自己制作栏位，请检测方框。制作的栏位会添加到已有的栏位中。", "如果要在此頁面上自己製作欄位，請偵測方框。製作的欄位會加入到既有的欄位中。", "이 페이지에 직접 입력란을 만들려면 상자를 찾으세요. 만든 입력란은 기존 입력란에 더해집니다.", "Pour créer vos propres champs sur cette page, détectez ses cases. Les champs créés s’ajoutent à ceux qu’il a déjà.", "Um stattdessen eigene Felder zu machen, die Kästchen erkennen lassen. Die neuen Felder kommen zu den vorhandenen hinzu."],
    "Ui_FillNewForm": ["Find boxes for a new form", "枠を検出して新しいフォームを作る", "检测方框以制作新表单", "偵測方框以製作新表單", "상자를 찾아 새 양식 만들기", "Détecter les cases pour un nouveau formulaire", "Kästchen für ein neues Formular erkennen"],
    "Ui_GroupFill": ["Fill & sign", "記入・署名", "填写与签名", "填寫與簽名", "작성·서명", "Remplir, signer", "Ausfüllen"],
    "Ui_GroupEdit": ["Edit", "編集", "编辑", "編輯", "편집", "Modifier", "Bearbeiten"],
    "Ui_GroupPages": ["Pages", "ページ", "页面", "頁面", "페이지", "Pages", "Seiten"],
    "Ui_GroupReview": ["Review", "レビュー", "审阅", "審閱", "검토", "Réviser", "Prüfen"],
    "Ui_GroupProtect": ["Protect", "保護", "保护", "保護", "보호", "Protéger", "Schützen"],
    "Ui_GroupOutput": ["Output", "仕上げ", "输出", "輸出", "마무리", "Sortie", "Ausgabe"],
    "Ui_HomeTitle": ["What would you like to do?", "何をしますか？", "你想做什么？", "你想做什麼？", "무엇을 하시겠어요?", "Que voulez-vous faire ?", "Was möchten Sie tun?"],
    "Ui_HomeLead": ["Drop a PDF anywhere in this window, or open one. Or start with a tool: Washi asks for the file it needs.", "PDF をこのウィンドウにドロップするか、開いてください。ツールから始めることもできます（必要なファイルは Washi が尋ねます）。", "把 PDF 拖到此窗口中，或打开一个。也可以从工具开始：Washi 会询问需要的文件。", "把 PDF 拖到此視窗中，或開啟一個。也可以從工具開始：Washi 會詢問需要的檔案。", "PDF를 이 창에 끌어 놓거나 여세요. 도구에서 시작해도 됩니다. 필요한 파일은 Washi가 묻습니다.", "Déposez un PDF dans cette fenêtre ou ouvrez-en un. Ou commencez par un outil : Washi demande le fichier nécessaire.", "Ein PDF in dieses Fenster ziehen oder eines öffnen. Oder mit einem Werkzeug beginnen: Washi fragt nach der Datei."],
    "Ui_MoreOptions": ["More options", "その他の設定", "更多选项", "更多選項", "추가 옵션", "Plus d’options", "Weitere Optionen"],
    "Ui_Shortcuts": ["Keyboard shortcuts", "キーボード操作", "键盘操作", "鍵盤操作", "키보드 단축키", "Raccourcis clavier", "Tastenkürzel"],
    "Ui_UpdTitle": ["Updates", "アップデート", "更新", "更新", "업데이트", "Mises à jour", "Updates"],
    "Ui_UpdCheck": ["Check for updates", "アップデートを確認", "检查更新", "檢查更新", "업데이트 확인", "Rechercher des mises à jour", "Nach Updates suchen"],
    "Ui_UpdChecking": ["Checking for updates…", "アップデートを確認しています…", "正在检查更新…", "正在檢查更新…", "업데이트를 확인하는 중…", "Recherche de mises à jour…", "Suche nach Updates …"],
    "Ui_UpdUpToDate": ["Washi is up to date.", "Washi は最新です。", "Washi 已是最新版本。", "Washi 已是最新版本。", "Washi가 최신 버전입니다.", "Washi est à jour.", "Washi ist auf dem neuesten Stand."],
    "Ui_UpdDownloading": ["Downloading Washi {0}… {1}%", "Washi {0} をダウンロードしています… {1}%", "正在下载 Washi {0}… {1}%", "正在下載 Washi {0}… {1}%", "Washi {0} 다운로드 중… {1}%", "Téléchargement de Washi {0}… {1} %", "Washi {0} wird geladen … {1} %"],
    "Ui_UpdReady": ["Washi {0} is ready. Restart Washi to start using it.", "Washi {0} の準備ができました。再起動すると使えます。", "Washi {0} 已准备好，重新启动即可使用。", "Washi {0} 已準備好，重新啟動即可使用。", "Washi {0} 준비가 끝났습니다. 다시 시작하면 사용할 수 있습니다.", "Washi {0} est prêt. Redémarrez Washi pour l’utiliser.", "Washi {0} ist bereit. Starten Sie Washi neu, um es zu verwenden."],
    "Ui_UpdFailed": ["Could not check for updates: {0}", "アップデートを確認できませんでした: {0}", "无法检查更新：{0}", "無法檢查更新：{0}", "업데이트를 확인하지 못했습니다: {0}", "Impossible de rechercher des mises à jour : {0}", "Updates konnten nicht geprüft werden: {0}"],
    "Ui_UpdNotInstalled": ["Updates work in the installed app; this copy was built from source.", "アップデートはインストールしたアプリで使えます。このコピーはソースからビルドされています。", "更新功能只在安装版中可用；当前这份是从源码构建的。", "更新功能只在安裝版中可用；目前這份是從原始碼建置的。", "업데이트는 설치한 앱에서 사용할 수 있습니다. 이 사본은 소스에서 빌드되었습니다.", "Les mises à jour fonctionnent dans l’app installée ; cette copie a été compilée depuis les sources.", "Updates gibt es in der installierten App; diese Kopie wurde aus dem Quellcode gebaut."],
    "Ui_UpdRestart": ["Restart to update", "再起動してアップデート", "重新启动以更新", "重新啟動以更新", "다시 시작하여 업데이트", "Redémarrer pour mettre à jour", "Neu starten und aktualisieren"],
    "Ui_UpdAuto": ["Check for updates automatically", "アップデートを自動で確認する", "自动检查更新", "自動檢查更新", "자동으로 업데이트 확인", "Rechercher automatiquement les mises à jour", "Automatisch nach Updates suchen"],
}
for _k, _v in _UI.items():
    assert len(_v) == len(LANGS), _k
    S[_k] = dict(zip(LANGS, _v))

HEAD = """<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by strings.py. Edit strings.py, not this file. -->
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""

here = os.path.dirname(os.path.abspath(__file__))
for lang in LANGS:
    missing = [k for k, v in S.items() if lang not in v]
    assert not missing, (lang, missing)
    name = "Strings.resx" if lang == "en" else f"Strings.{lang}.resx"
    with open(os.path.join(here, name), "w", encoding="utf-8") as f:
        f.write(HEAD)
        for key, tr in S.items():
            f.write(f'  <data name="{key}" xml:space="preserve"><value>{escape(tr[lang])}</value></data>\n')
        f.write("</root>\n")
print("wrote", len(LANGS), "files,", len(S), "strings each")
