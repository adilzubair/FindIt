using FindIt.Shared;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace FindIt.Runtime;

internal sealed class FaceProcessor : IDisposable
{
    private readonly RuntimePaths paths;
    // Creating a detector parses its model. Photos come in a few sizes (orientation, camera),
    // so one detector is kept per input size instead of being recreated on every change.
    private readonly Dictionary<Size,FaceDetectorYN> detectors=[];
    private Net? recognizer;
    internal bool Available=>paths.Model("face_detection_yunet_2023mar.onnx") is not null&&paths.Model("face_recognition_sface_2021dec.onnx") is not null;
    internal FaceProcessor(RuntimePaths paths)=>this.paths=paths;
    internal IReadOnlyList<DetectedFace> Detect(Mat original)
    {
        if(!Available)return [];
        if(recognizer is null){ModelIntegrity.Verify(paths.Model("face_detection_yunet_2023mar.onnx")!,"face_detection_yunet_2023mar.onnx");ModelIntegrity.Verify(paths.Model("face_recognition_sface_2021dec.onnx")!,"face_recognition_sface_2021dec.onnx");}
        recognizer??=CvDnn.ReadNetFromOnnx(File.ReadAllBytes(paths.Model("face_recognition_sface_2021dec.onnx")!));
        using var scaled=new Mat();var ratio=Math.Min(1d,1280d/Math.Max(original.Width,original.Height));
        Cv2.Resize(original,scaled,new Size((int)(original.Width*ratio),(int)(original.Height*ratio)));
        if(!detectors.TryGetValue(scaled.Size(),out var detector))
        {
            if(detectors.Count>=8){foreach(var stale in detectors.Values)stale.Dispose();detectors.Clear();}
            detectors[scaled.Size()]=detector=FaceDetectorYN.Create(paths.Model("face_detection_yunet_2023mar.onnx")!,"",scaled.Size(),.85f,.3f,5000);
        }
        using var detected=new Mat();detector.Detect(scaled,detected);
        var results=new List<DetectedFace>();
        var rows=detected.Rows;
        var row=new float[15];
        for(var i=0;i<rows;i++)
        {
            System.Runtime.InteropServices.Marshal.Copy(detected.Ptr(i),row,0,row.Length);
            var width=row[2];var height=row[3];
            if(Math.Min(width,height)<24)continue;
            using var aligned=Align(scaled,row);
            using var blob=CvDnn.BlobFromImage(aligned,1,new Size(112,112),new Scalar(0,0,0),true,false);
            recognizer!.SetInput(blob);using var feature=recognizer.Forward();
            var count=checked((int)feature.Total());var vector=new float[count];System.Runtime.InteropServices.Marshal.Copy(feature.Data,vector,0,count);
            var norm=Math.Sqrt(vector.Sum(v=>(double)v*v));if(norm<=0||!double.IsFinite(norm))continue;
            for(var j=0;j<count;j++)vector[j]/=(float)norm;
            var score=row[14];Cv2.ImEncode(".jpg",aligned,out var portrait,[new ImageEncodingParam(ImwriteFlags.JpegQuality,85)]);
            results.Add(new(vector,row[0]/scaled.Width,row[1]/scaled.Height,width/scaled.Width,height/scaled.Height,score,portrait));
        }
        return results;
    }
    private static Mat Align(Mat image,float[] face)
    {
        // Least-squares orientation-preserving similarity fit to SFace's five-point template.
        double[] u=[38.2946,73.5318,56.0252,41.5493,70.7299],v=[51.6963,51.5014,71.7366,92.3655,92.2041];
        var x=new double[5];var y=new double[5];for(var i=0;i<5;i++){x[i]=face[4+i*2];y[i]=face[5+i*2];}
        var cx=x.Average();var cy=y.Average();var cu=u.Average();var cv=v.Average();double denominator=0,a=0,b=0;
        for(var i=0;i<5;i++){var dx=x[i]-cx;var dy=y[i]-cy;var du=u[i]-cu;var dv=v[i]-cv;denominator+=dx*dx+dy*dy;a+=dx*du+dy*dv;b+=dx*dv-dy*du;}
        if(denominator<=0)throw new InvalidDataException("Invalid face landmarks.");a/=denominator;b/=denominator;
        using var transform=new Mat(2,3,MatType.CV_64FC1);
        transform.Set(0,0,a);transform.Set(0,1,-b);transform.Set(0,2,cu-a*cx+b*cy);
        transform.Set(1,0,b);transform.Set(1,1,a);transform.Set(1,2,cv-b*cx-a*cy);
        var result=new Mat();Cv2.WarpAffine(image,result,transform,new Size(112,112));return result;
    }
    public void Dispose(){foreach(var detector in detectors.Values)detector.Dispose();detectors.Clear();recognizer?.Dispose();}
}
