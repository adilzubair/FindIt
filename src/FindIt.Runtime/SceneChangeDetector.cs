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
        Array.Clear(histogram);
        for(int i=0;i<thumbnail.Length;i++)histogram[i%3*16+thumbnail[i]/16]++;
        bool cut=false;
        if(previous is not null)
        {
            long difference=0;int changed=0;
            for(int i=0;i<thumbnail.Length;i+=3)
            {
                int pixel=Math.Abs(thumbnail[i]-previous[i])+Math.Abs(thumbnail[i+1]-previous[i+1])+Math.Abs(thumbnail[i+2]-previous[i+2]);
                difference+=pixel;if(pixel>3*255*.18)changed++;
            }
            int histogramDifference=0;
            for(int i=0;i<histogram.Length;i++)histogramDifference+=Math.Abs(histogram[i]-previousHistogram[i]);
            // A palette change plus a broad pixel change suppresses ordinary movement within the same shot.
            cut=difference/(thumbnail.Length*255d)>=.18&&changed/(32d*18)>=.60&&histogramDifference/(thumbnail.Length*2d)>=.35;
        }
        previous??=new byte[thumbnail.Length];thumbnail.CopyTo(previous);histogram.CopyTo(previousHistogram,0);
        return cut;
    }
}
