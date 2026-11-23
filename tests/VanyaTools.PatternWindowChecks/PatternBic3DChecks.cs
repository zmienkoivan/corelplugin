using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using VanyaTools.Native;

internal static class PatternBic3DChecks
{
    internal static void Run(Action<bool,string> check)
    {
        double width=PatternBic3DModel.BodyWidth(20),depth=12;
        var profile=Enumerable.Range(0,10001).Select(i=>PatternBic3DModel.Profile(width,depth,i/10000d)).ToArray();
        double perimeter=profile.Zip(profile.Skip(1),(a,b)=>(a-b).Length).Sum();
        check(Math.Abs(perimeter-Math.PI*20)<.0001 && (profile[0]-profile[10000]).Length<1e-9,"BIC profile perimeter does not match its print template");
        check(Math.Abs(profile[5000].X)<1e-9 && Math.Abs(profile[5000].Y-6)<1e-9,"BIC front does not correspond to template centre");
        var mesh=PatternBic3DModel.Tube(width,depth,0,64);
        for(int i=0;i<mesh.TriangleIndices.Count;i+=3)
        {
            Point3D a=mesh.Positions[mesh.TriangleIndices[i]],b=mesh.Positions[mesh.TriangleIndices[i+1]],c=mesh.Positions[mesh.TriangleIndices[i+2]];
            var normal=Vector3D.CrossProduct(b-a,c-a);var middle=new Vector3D((a.X+b.X+c.X)/3,0,(a.Z+b.Z+c.Z)/3);
            check(Vector3D.DotProduct(normal,middle)>0,"BIC body triangles face inside");
        }
        check(mesh.TextureCoordinates[0]==new Point(0,1) && mesh.TextureCoordinates[129]==new Point(0,0),"BIC texture is upside down");
        var pixels=new byte[8*8*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=30;pixels[i+1]=90;pixels[i+2]=230;pixels[i+3]=255;}
        var image=BitmapSource.Create(8,8,96,96,PixelFormats.Bgra32,null,pixels,32);image.Freeze();
        var asset=new PatternAsset{Id=1,Size=new PatternPoint(10,10),Footprint=new PatternFootprint(new PatternPoint(10,10)),Preview=image};
        var bottle=new PatternBottle(20,20,64);var frame=PatternBottleLayout.Frame(bottle,new PatternPoint(0,0));
        var draft=new PatternDraft{Frame=frame,Motifs=new[]{asset},Options=new PatternFillOptions(),Items=new List<PatternPlacement>{new PatternPlacement{Motif=0,Center=new PatternPoint(1,32),Size=10},new PatternPlacement{Motif=0,Center=new PatternPoint(bottle.Width/2,50),Size=15}}};
        var texture=PatternBic3DTexture.Create(draft,null);int stride=texture.PixelWidth*4;var data=new byte[stride*texture.PixelHeight];texture.CopyPixels(data,stride,0);
        bool Red(int x,int y)=>data[y*stride+x*4+2]>180 && data[y*stride+x*4]<80;
        check(Red(0,texture.PixelHeight/2) && Red(texture.PixelWidth-1,texture.PixelHeight/2),"Texture does not wrap a motif across the print seam");
        check(Red(texture.PixelWidth/2,(int)(14/64d*texture.PixelHeight)) && !Red(texture.PixelWidth/2,(int)(50/64d*texture.PixelHeight)),"Texture uses reversed print coordinates");
        var scene=new PatternBic3DScene();scene.Set(bottle,texture);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya BIC 3D checks"){Width=650,Height=780,WindowStyle=0}))
        {
            source.RootVisual=scene;scene.Measure(new Size(650,780));scene.Arrange(new Rect(0,0,650,780));scene.UpdateLayout();
            string directory=Environment.GetEnvironmentVariable("VANYA_PATTERN_RENDER");
            foreach(int yaw in new[]{0,35,180})
            {
                scene.ShowSeam=yaw==180;scene.View(yaw);var render=new RenderTargetBitmap(650,780,96,96,PixelFormats.Pbgra32);render.Render(scene);
                var bytes=new byte[650*780*4];render.CopyPixels(bytes,650*4,0);int red=0;for(int i=0;i<bytes.Length;i+=4)if(bytes[i+2]>bytes[i]*1.4 && bytes[i+2]>bytes[i+1]*1.4)red++;
                check(red>100,"BIC 3D did not render the current red artwork at view "+yaw);
                if(!String.IsNullOrEmpty(directory)){Directory.CreateDirectory(directory);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(render));using(var file=File.Create(Path.Combine(directory,"bic-3d-"+yaw+".png")))e.Save(file);}
            }
            check(ReferenceEquals(scene.Texture,texture),"3D replaced the supplied current pattern texture");
        }
        var cone=new PatternDraft{Frame=PatternBottleLayout.Frame(new PatternBottle(20,25,64),new PatternPoint(0,0))};bool rejected=false;try{PatternBic3DTexture.Create(cone,null);}catch(InvalidOperationException){rejected=true;}check(rejected,"BIC preview silently distorted a tapered template");
    }
}
