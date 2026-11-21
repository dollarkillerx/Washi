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
    "Kind_text": {"en": "text", "ja": "テキスト", "zh-Hans": "文本", "zh-Hant": "文字", "ko": "텍스트", "fr": "texte", "de": "Text"},
    "Kind_amount": {"en": "amount", "ja": "金額", "zh-Hans": "金额", "zh-Hant": "金額", "ko": "금액", "fr": "montant", "de": "Betrag"},
    "Kind_comb": {"en": "comb", "ja": "マス目", "zh-Hans": "分格", "zh-Hant": "分格", "ko": "칸 나눔", "fr": "cases", "de": "Kästchen"},
    "Kind_checkbox": {"en": "checkbox", "ja": "チェックボックス", "zh-Hans": "复选框", "zh-Hant": "核取方塊", "ko": "체크박스", "fr": "case à cocher", "de": "Kontrollkästchen"},
}

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
