# Used by report-preview.ps1. Runs under OpenOffice 4's bundled Python 2.7: opens the report hidden, removes every sheet but the first
# (in memory only), and exports that sheet to PDF, to see the cover page as OpenOffice Calc shows it.
import sys, time, uno
from com.sun.star.beans import PropertyValue

src, dst = sys.argv[1], sys.argv[2]

def prop(name, value):
    p = PropertyValue()
    p.Name = name
    p.Value = value
    return p

local = uno.getComponentContext()
resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
ctx = None
for _ in range(60):
    try:
        ctx = resolver.resolve("uno:socket,host=127.0.0.1,port=2083;urp;StarOffice.ComponentContext")
        break
    except Exception:
        time.sleep(1)
if ctx is None:
    raise SystemExit("could not connect to soffice")

desktop = ctx.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
doc = desktop.loadComponentFromURL(uno.systemPathToFileUrl(src), "_blank", 0, (prop("Hidden", True), prop("ReadOnly", True)))
sheets = doc.getSheets()
names = sheets.getElementNames()
for name in names[1:]:
    sheets.removeByName(name)
doc.storeToURL(uno.systemPathToFileUrl(dst), (prop("FilterName", "calc_pdf_Export"),))
doc.close(True)
try:
    desktop.terminate()
except Exception:
    pass
print("ok")
