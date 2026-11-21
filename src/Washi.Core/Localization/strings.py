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
        "en": "{0} is protected against changes (it needs the owner password). Washi does not remove protection: ask the author for an unprotected copy.",
        "ja": "{0} は変更できないよう保護されています（所有者パスワードが必要です）。Washi は保護を解除しません。作成者に保護のないファイルを依頼してください。",
        "zh-Hans": "{0} 设置了禁止修改的保护（需要所有者密码）。Washi 不会解除保护，请向作者索取未加保护的文件。",
        "zh-Hant": "{0} 設有禁止修改的保護（需要擁有者密碼）。Washi 不會解除保護，請向作者索取未加保護的檔案。",
        "ko": "{0} 은(는) 변경할 수 없도록 보호되어 있습니다 (소유자 암호 필요). Washi 는 보호를 해제하지 않습니다. 작성자에게 보호되지 않은 파일을 요청하세요.",
        "fr": "{0} est protégé contre les modifications (mot de passe propriétaire requis). Washi ne retire pas la protection : demandez une copie non protégée à l’auteur.",
        "de": "{0} ist gegen Änderungen geschützt (Besitzerkennwort erforderlich). Washi hebt keinen Schutz auf: Bitten Sie den Ersteller um eine ungeschützte Kopie.",
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
        "en": "Something is printed here (drawn as graphics, not text).",
        "ja": "ここには何かが印刷されています（文字ではなく図形として描かれています）。",
        "zh-Hans": "这里印有内容（以图形而非文字的形式绘制）。",
        "zh-Hant": "這裡印有內容（以圖形而非文字的形式繪製）。",
        "ko": "여기에 무언가 인쇄되어 있습니다 (텍스트가 아닌 그래픽으로 그려짐).",
        "fr": "Quelque chose est imprimé ici (dessiné en graphisme, pas en texte).",
        "de": "Hier ist etwas gedruckt (als Grafik gezeichnet, nicht als Text).",
    },
    "Ui_PrefilledUnreadable": {
        "en": "Text is printed here, but the PDF's text layer cannot be read (its fonts have no Unicode mapping). OCR can read it.",
        "ja": "ここには文字が印刷されていますが、PDF の文字情報を読み取れません（フォントに Unicode の対応表がありません）。OCR なら読み取れます。",
        "zh-Hans": "这里印有文字，但 PDF 的文字层无法读取（字体没有 Unicode 对照表）。OCR 可以识别。",
        "zh-Hant": "這裡印有文字，但 PDF 的文字層無法讀取（字型沒有 Unicode 對照表）。OCR 可以辨識。",
        "ko": "여기에 글자가 인쇄되어 있지만 PDF 의 텍스트 레이어를 읽을 수 없습니다 (글꼴에 유니코드 매핑이 없음). OCR 로 읽을 수 있습니다.",
        "fr": "Du texte est imprimé ici, mais la couche texte du PDF est illisible (ses polices n’ont pas de correspondance Unicode). L’OCR peut le lire.",
        "de": "Hier ist Text gedruckt, aber die Textebene des PDFs ist nicht lesbar (die Schriften haben keine Unicode-Zuordnung). OCR kann ihn lesen.",
    },
    "Kind_text": {"en": "text", "ja": "テキスト", "zh-Hans": "文本", "zh-Hant": "文字", "ko": "텍스트", "fr": "texte", "de": "Text"},
    "Kind_amount": {"en": "amount", "ja": "金額", "zh-Hans": "金额", "zh-Hant": "金額", "ko": "금액", "fr": "montant", "de": "Betrag"},
    "Kind_comb": {"en": "comb", "ja": "マス目", "zh-Hans": "分格", "zh-Hant": "分格", "ko": "칸 나눔", "fr": "cases", "de": "Kästchen"},
    "Kind_checkbox": {"en": "checkbox", "ja": "チェックボックス", "zh-Hans": "复选框", "zh-Hant": "核取方塊", "ko": "체크박스", "fr": "case à cocher", "de": "Kontrollkästchen"},
    "UsagePdf": {
        "en": 'Pages and OCR:\n  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      join files (form fields are kept)\n  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n                                          make scanned pages searchable (an invisible text layer; the page image is unchanged)',
        "ja": 'ページと OCR:\n  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      ファイルを結合（記入欄はそのまま残ります）\n  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n                                          スキャンしたページを検索できるようにする（見えない文字層を追加。ページの画像は変わりません）',
        "zh-Hans": '页面与 OCR：\n  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      合并文件（保留表单栏位）\n  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n                                          让扫描页可以搜索（加一层看不见的文字，页面图像不变）',
        "zh-Hant": '頁面與 OCR：\n  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      合併檔案（保留表單欄位）\n  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n                                          讓掃描頁可以搜尋（加一層看不見的文字，頁面影像不變）',
        "ko": '페이지와 OCR:\n  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      파일 합치기 (입력란 유지)\n  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dir]\n  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n                                          스캔한 페이지를 검색 가능하게 (보이지 않는 텍스트 레이어 추가, 페이지 이미지는 그대로)',
        "fr": 'Pages et OCR :\n  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      assemble des fichiers (les champs de formulaire sont conservés)\n  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o dossier]\n  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n                                          rend les pages numérisées cherchables (couche de texte invisible ; l’image de la page ne change pas)',
        "de": 'Seiten und OCR:\n  washi pdf merge <a.pdf> <b.pdf> ... -o out.pdf      Dateien zusammenfügen (Formularfelder bleiben erhalten)\n  washi pdf split <in.pdf> [--pages 1-3,5|each] [-o Ordner]\n  washi ocr <in.pdf> [-o out.pdf] [--engine auto|vision|tesseract|ai] [--lang ja,en] [--pages 1-3] [--force] [--text out.txt] [--yes]\n                                          gescannte Seiten durchsuchbar machen (unsichtbare Textebene; das Seitenbild bleibt unverändert)',
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
    "Ui_AiModelCaption": ["AI MODEL", "AI モデル", "AI 模型", "AI 模型", "AI 모델", "MODÈLE D’IA", "KI-MODELL"],
    "Ui_AiModelNote": ["Change it on the command line: washi ai config <provider> <model>. API keys stay in the Keychain.",
                       "コマンドラインで変更します: washi ai config <プロバイダ> <モデル>。API キーはキーチェーンに保存されます。",
                       "在命令行中更改：washi ai config <提供方> <模型>。API key 保存在钥匙串中。",
                       "在命令列中變更：washi ai config <提供者> <模型>。API key 儲存在鑰匙圈中。",
                       "명령줄에서 변경합니다: washi ai config <제공자> <모델>. API 키는 키체인에 보관됩니다.",
                       "Changez-le en ligne de commande : washi ai config <fournisseur> <modèle>. Les clés d’API restent dans le trousseau.",
                       "Auf der Kommandozeile ändern: washi ai config <Anbieter> <Modell>. API-Schlüssel bleiben im Schlüsselbund."],
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
    "Ui_ToolAsk": ["Ask AI", "AI に質問", "问 AI", "問 AI", "AI 에게 질문", "Demander à l’IA", "KI fragen"],
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
    "Ui_EditKeys": ["V select · F draw a field · Tab next field · Enter rename · Delete remove · arrows nudge (Shift: 5 pt)",
                    "V 選択 · F 記入欄を描く · Tab 次の記入欄 · Enter 名前を変更 · Delete 削除 · 矢印 微調整（Shift: 5 pt）",
                    "V 选择 · F 画栏位 · Tab 下一个栏位 · Enter 改名 · Delete 删除 · 方向键 微调（Shift：5 pt）",
                    "V 選取 · F 繪製欄位 · Tab 下一個欄位 · Enter 改名 · Delete 刪除 · 方向鍵 微調（Shift：5 pt）",
                    "V 선택 · F 입력란 그리기 · Tab 다음 입력란 · Enter 이름 변경 · Delete 삭제 · 화살표 미세 조정 (Shift: 5 pt)",
                    "V sélection · F dessiner un champ · Tab champ suivant · Entrée renommer · Suppr retirer · flèches ajuster (Maj : 5 pt)",
                    "V Auswahl · F Feld zeichnen · Tab nächstes Feld · Enter umbenennen · Entf löschen · Pfeile verschieben (Umschalt: 5 pt)"],
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
    "Ui_DropTitle": ["Open a PDF", "PDF を開く", "打开 PDF", "開啟 PDF", "PDF 열기", "Ouvrir un PDF", "PDF öffnen"],
    "Ui_DropLead": ["Drop a PDF here, or choose one. Then pick a tool on the left.",
                    "ここに PDF をドロップするか、選んでください。次に左のツールを選びます。",
                    "把 PDF 拖到这里，或选择一个文件。然后在左侧选择工具。",
                    "把 PDF 拖到這裡，或選擇一個檔案。然後在左側選擇工具。",
                    "여기에 PDF 를 끌어다 놓거나 선택하세요. 그다음 왼쪽에서 도구를 고르세요.",
                    "Déposez un PDF ici ou choisissez-en un. Puis choisissez un outil à gauche.",
                    "PDF hier ablegen oder auswählen. Dann links ein Werkzeug wählen."],
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
    "Ui_CombineLead": ["Puts PDFs together in the order below. Form fields stay fillable.",
                       "下の順番で PDF をまとめます。記入欄はそのまま記入できます。",
                       "按下面的顺序合并 PDF。表单栏位仍可填写。",
                       "依下方順序合併 PDF。表單欄位仍可填寫。",
                       "아래 순서대로 PDF 를 합칩니다. 입력란은 계속 입력할 수 있습니다.",
                       "Assemble les PDF dans l’ordre ci-dessous. Les champs restent remplissables.",
                       "Fügt die PDFs in der Reihenfolge unten zusammen. Formularfelder bleiben ausfüllbar."],
    "Ui_AddFiles": ["Add files…", "ファイルを追加…", "添加文件…", "加入檔案…", "파일 추가…", "Ajouter…", "Dateien hinzufügen…"],
    "Ui_MoveUp": ["Move up", "上へ", "上移", "上移", "위로", "Monter", "Nach oben"],
    "Ui_MoveDown": ["Move down", "下へ", "下移", "下移", "아래로", "Descendre", "Nach unten"],
    "Ui_CombineEmpty": ["Add two or more PDFs, or drop them here.", "PDF を 2 つ以上追加するか、ここにドロップしてください。", "添加两个或更多 PDF，或拖到这里。", "加入兩個或更多 PDF，或拖到這裡。", "PDF 를 두 개 이상 추가하거나 여기로 끌어다 놓으세요.", "Ajoutez au moins deux PDF, ou déposez-les ici.", "Zwei oder mehr PDFs hinzufügen oder hier ablegen."],
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
    "Ui_AskTitle": ["Ask about this document", "この文書について質問", "询问这份文件", "詢問這份文件", "이 문서에 대해 질문", "Questions sur ce document", "Fragen zu diesem Dokument"],
    "Ui_AskLead": ["The AI model reads the document and answers. Washi shows what it will send and asks first.",
                   "AI モデルが文書を読んで答えます。送信する内容を表示し、先に確認します。",
                   "AI 模型阅读文件并回答。Washi 会先列出要发送的内容并请你确认。",
                   "AI 模型閱讀文件並回答。Washi 會先列出要傳送的內容並請你確認。",
                   "AI 모델이 문서를 읽고 답합니다. Washi 는 보낼 내용을 보여 주고 먼저 확인합니다.",
                   "Le modèle d’IA lit le document et répond. Washi montre ce qui sera envoyé et demande d’abord.",
                   "Das KI-Modell liest das Dokument und antwortet. Washi zeigt vorher, was gesendet wird, und fragt."],
    "Ui_AskWatermark": ["e.g. Which boxes must I fill in?", "例: どの欄を記入すればいい？", "例如：我需要填哪些栏？", "例如：我需要填哪些欄？", "예: 어떤 칸을 채워야 하나요?", "ex. : Quelles cases dois-je remplir ?", "z. B.: Welche Felder muss ich ausfüllen?"],
    "Ui_AskRun": ["Ask (Enter)", "質問する（Enter）", "提问（Enter）", "提問（Enter）", "질문 (Enter)", "Demander (Entrée)", "Fragen (Enter)"],
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
