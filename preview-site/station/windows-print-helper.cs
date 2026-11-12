using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

[assembly:System.Reflection.AssemblyTitle("Vanya Orders Print Helper")]
[assembly:System.Reflection.AssemblyVersion("1.0.0.0")]

public sealed class LabelBarcode {
    public int[] widths { get; set; }
    public double xMm { get; set; } public double yMm { get; set; }
    public double widthMm { get; set; } public double heightMm { get; set; }
    public double paddingYmm { get; set; } public int quietModules { get; set; }
}
public sealed class LabelPayload {
    public string printerName { get; set; } public string paperMode { get; set; }
    public string imagePath { get; set; } public string previewPath { get; set; }
    public string[] imagePaths { get; set; } public bool document { get; set; }
    public double labelWidthMm { get; set; }
    public string jobId { get; set; } public double labelHeightMm { get; set; }
    public float previewDpi { get; set; } public LabelBarcode barcodeLayout { get; set; }
}

internal static class OrderPrintHelper {
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static bool Physical(string name) { return !String.IsNullOrWhiteSpace(name) && !Regex.IsMatch(name,"PDF|OneNote|Fax|XPS",RegexOptions.IgnoreCase); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    private static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        string stage = "prepare";
        try {
            Require(args.Length > 0, "Missing print action."); string action = args[0];
            if (action == "list") {
                var printers = new List<object>(); string current = new PrinterSettings().PrinterName;
                foreach (string name in PrinterSettings.InstalledPrinters) if (Physical(name)) printers.Add(new {name=name,isDefault=name==current});
                Console.WriteLine(Json.Serialize(new {ok=true,printers=printers})); return 0;
            }
            Require(action=="print" || action=="preview" || action=="raster-preview", "Unknown print action.");
            Require(args.Length==2, "Missing print payload.");
            LabelPayload payload = Json.Deserialize<LabelPayload>(File.ReadAllText(args[1],Encoding.UTF8));
            Require(payload!=null,"Invalid print payload.");
            double width = payload.document ? payload.labelWidthMm : payload.paperMode=="mini" ? 45 : 80;
            double height = payload.paperMode=="mini" ? 22.5 : payload.labelHeightMm==0 ? 110 : payload.labelHeightMm;
            Require(payload.paperMode=="mini" || payload.paperMode=="label" || payload.paperMode=="sheet", "Unknown paper mode.");
            Require(payload.document ? (payload.paperMode=="sheet" ? width==105 && height==148 : width==80 && (height==110 || height==120)) : payload.paperMode=="mini" ? height==22.5 : height==110 || height==120, "Invalid label size.");
            string[] imagePaths = payload.document ? payload.imagePaths : new[] {payload.imagePath};
            Require(imagePaths!=null && imagePaths.Length>=1 && imagePaths.Length<=8,"Invalid page count.");
            int pageIndex=0;
            CheckBarcode(payload.barcodeLayout,width,height,payload.paperMode=="mini");
            using (Image image = Image.FromFile(imagePaths[0])) {
                if (action=="raster-preview") {
                    float dpi=payload.previewDpi==0 ? 203.2f : payload.previewDpi;
                    Require(Finite(dpi) && dpi>=150 && dpi<=1200,"Invalid preview DPI.");
                    using (var bitmap=new Bitmap((int)Math.Round(width/25.4*dpi),(int)Math.Round(height/25.4*dpi))) {
                        bitmap.SetResolution(dpi,dpi);
                        using (Graphics graphics=Graphics.FromImage(bitmap)) Paint(graphics,image,payload.barcodeLayout,width,height,0,0,payload.document);
                        bitmap.Save(payload.previewPath,ImageFormat.Png);
                    }
                    Console.WriteLine(Json.Serialize(new {ok=true,labelWidthMm=width,labelHeightMm=height,dpi=dpi,preview=true}));return 0;
                }
                using (var document=new PrintDocument()) {
                    document.PrinterSettings.PrinterName=payload.printerName;
                    Require(document.PrinterSettings.IsValid,"The selected Windows printer is unavailable.");
                    Require(Physical(payload.printerName),"Select a physical printer.");
                    document.DocumentName="Vanya Label "+payload.jobId;
                    document.PrinterSettings.Copies=1;document.PrinterSettings.PrintToFile=false;document.PrinterSettings.Duplex=Duplex.Simplex;
                    document.DefaultPageSettings.Landscape=false;document.DefaultPageSettings.Margins=new Margins(0,0,0,0);document.OriginAtMargins=false;
                    PaperSize paper=null;
                    int w=(int)Math.Round(width/25.4*100),h=(int)Math.Round(height/25.4*100);
                    foreach (PaperSize item in document.PrinterSettings.PaperSizes) {
                        if (payload.paperMode=="sheet" ? item.Kind==PaperKind.A4 : Math.Abs(item.Width-w)<=2 && Math.Abs(item.Height-h)<=2) {paper=item;break;}
                    }
                    Require(paper!=null || payload.paperMode!="sheet","This printer driver does not support A4 paper.");
                    if (paper==null) paper=new PaperSize("Vanya Label",w,h);
                    document.DefaultPageSettings.PaperSize=paper;
                    document.PrintPage+=delegate(object sender, PrintPageEventArgs page) {
                        Graphics graphics=page.Graphics;graphics.PageUnit=GraphicsUnit.Pixel;
                        graphics.TranslateTransform(-(float)Math.Round(page.PageSettings.HardMarginX*graphics.DpiX/100),-(float)Math.Round(page.PageSettings.HardMarginY*graphics.DpiY/100));
                        double offset=payload.paperMode=="sheet" ? 10 : 0;
                        using (Image pageImage=Image.FromFile(imagePaths[pageIndex])) Paint(graphics,pageImage,payload.barcodeLayout,width,height,offset,offset,payload.document);
                        pageIndex++;page.HasMorePages=pageIndex<imagePaths.Length;
                    };
                    document.PrintController=action=="preview" ? (PrintController)new PreviewPrintController() : new StandardPrintController();
                    stage="spooling";document.Print();
                    if (action=="preview") {
                        PreviewPageInfo[] pages=((PreviewPrintController)document.PrintController).GetPreviewPageInfo();
                        try {Require(pages.Length==imagePaths.Length,"Unexpected page count.");pages[0].Image.Save(payload.previewPath,ImageFormat.Png);}
                        finally {foreach (PreviewPageInfo page in pages) page.Image.Dispose();}
                    }
                }
            }
            Console.WriteLine(Json.Serialize(new {ok=true,printerName=payload.printerName,paperMode=payload.paperMode,labelWidthMm=width,labelHeightMm=height,preview=action=="preview"}));return 0;
        } catch (Exception error) {Console.WriteLine(Json.Serialize(new {ok=false,stage=stage,error=error.Message}));return 1;}
    }

    private static void CheckBarcode(LabelBarcode barcode, double width, double height, bool mini) {
        if (barcode==null) return;
        Require(!mini && barcode.widths!=null && barcode.widths.Length>=3 && barcode.widths.Length<=1000 && barcode.quietModules>=10 && barcode.quietModules<=100,"Invalid barcode layout.");
        foreach (int run in barcode.widths) Require(run>=1 && run<=4,"Invalid barcode module width.");
        foreach (double value in new[] {barcode.xMm,barcode.yMm,barcode.widthMm,barcode.heightMm,barcode.paddingYmm}) Require(Finite(value) && value>=0,"Invalid barcode geometry.");
        Require(barcode.widthMm>0 && barcode.heightMm>2*barcode.paddingYmm && barcode.xMm+barcode.widthMm<=width && barcode.yMm+barcode.heightMm<=height,"Barcode does not fit the label.");
    }
    private static void Paint(Graphics graphics, Image image, LabelBarcode barcode, double width, double height, double leftMm, double topMm, bool document) {
        graphics.PageUnit=GraphicsUnit.Pixel;graphics.PageScale=1;
        double dx=graphics.DpiX/25.4,dy=graphics.DpiY/25.4;
        int left=(int)Math.Round(leftMm*dx),top=(int)Math.Round(topMm*dy);
        graphics.Clear(Color.White);graphics.InterpolationMode=document ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(image,new Rectangle(left,top,(int)Math.Round(width*dx),(int)Math.Round(height*dy)));
        if (barcode==null) return;
        graphics.SmoothingMode=SmoothingMode.None;graphics.PixelOffsetMode=PixelOffsetMode.None;
        int x=left+(int)Math.Round(barcode.xMm*dx),y=top+(int)Math.Round(barcode.yMm*dy);
        int w=(int)Math.Round(barcode.widthMm*dx),h=(int)Math.Round(barcode.heightMm*dy),padding=(int)Math.Round(barcode.paddingYmm*dy);
        int modules=2*barcode.quietModules;foreach (int run in barcode.widths) modules+=run;
        int dots=(int)Math.Floor((double)w/modules);Require(dots>=2,"The barcode is too wide for this printer resolution.");
        graphics.FillRectangle(Brushes.White,x-1,y-1,w+2,h+2);
        int barX=x+(int)Math.Floor((w-modules*dots)/2.0)+barcode.quietModules*dots;
        for (int i=0;i<barcode.widths.Length;i++) {int run=barcode.widths[i]*dots;if (i%2==0) graphics.FillRectangle(Brushes.Black,barX,y+padding,run,h-2*padding);barX+=run;}
    }
}
