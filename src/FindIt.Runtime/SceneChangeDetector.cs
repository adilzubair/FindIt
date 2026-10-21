namespace FindIt.Runtime;

/// <summary>Bounded, conservative cut detection on a 32×18 BGR thumbnail.</summary>
internal sealed class SceneChangeDetector
{
    private byte[]? previous;
    private readonly int[] histogram=new int[48];
    private readonly int[] previousHistogram=new int[48];

    public bool Observe(ReadOnlySpan<byte> thumbnail)
    {
        if(thumbnail.Length!=32*18*3)throw new ArgumentException("A 32 by 18 BGR thumbnail is required.",nameof(thumbnail));
        bool cut=false;
        if(previous is not null)
        {
            Array.Clear(histogram);Array.Clear(previousHistogram);
            long difference=0;int changed=0,active=0;
            for(int i=0;i<thumbnail.Length;i+=3)
            {
                // Stable black padding is not evidence that two shots are alike. Mixed
                // aspect ratios otherwise hid all cuts in the real letterboxed montage.
                if(Math.Max(Math.Max(thumbnail[i],thumbnail[i+1]),thumbnail[i+2])<=20&&Math.Max(Math.Max(previous[i],previous[i+1]),previous[i+2])<=20)continue;
                active++;
                int pixel=Math.Abs(thumbnail[i]-previous[i])+Math.Abs(thumbnail[i+1]-previous[i+1])+Math.Abs(thumbnail[i+2]-previous[i+2]);
                difference+=pixel;if(pixel>3*255*.18)changed++;
                for(int channel=0;channel<3;channel++)
                {
                    histogram[channel*16+thumbnail[i+channel]/16]++;
                    previousHistogram[channel*16+previous[i+channel]/16]++;
                }
            }
            int histogramDifference=0;
            for(int i=0;i<histogram.Length;i++)histogramDifference+=Math.Abs(histogram[i]-previousHistogram[i]);
            // A palette change plus a broad pixel change suppresses ordinary movement within the same shot.
            cut=active>=64&&difference/(active*3d*255)>=.18&&changed/(double)active>=.45&&histogramDifference/(active*6d)>=.35;
        }
        previous??=new byte[thumbnail.Length];thumbnail.CopyTo(previous);
        return cut;
    }
}
