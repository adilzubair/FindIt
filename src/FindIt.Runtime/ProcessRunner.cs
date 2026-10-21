using System.Diagnostics;

namespace FindIt.Runtime;

internal static class ProcessRunner
{
    internal static Process Start(string executable,IEnumerable<string> arguments,bool binaryOutput=false)
    {
        // Never inherit the worker's framed RPC stdin. FFmpeg's interactive reader can consume it or block on it.
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        var p=new Process{StartInfo=info};p.Start();p.StandardInput.Close();return p;
    }
    internal static async Task<string> CaptureAsync(string executable,IEnumerable<string> arguments,CancellationToken token)
    {
        using var process=Start(executable,arguments);
        using var registration=token.Register(()=>Kill(process));
        var output=process.StandardOutput.ReadToEndAsync(token);
        var error=ReadTailAsync(process.StandardError,token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var result=await output.ConfigureAwait(false);var diagnostic=await error.ConfigureAwait(false);
        if(process.ExitCode!=0)throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with {process.ExitCode}: {diagnostic}");
        return result;
    }
    internal static async Task<string> ReadTailAsync(StreamReader reader,CancellationToken token)
    {
        var buffer=new char[4096];var tail="";int read;
        while((read=await reader.ReadAsync(buffer,token).ConfigureAwait(false))>0){tail+=new string(buffer,0,read);if(tail.Length>8192)tail=tail[^8192..];}
        return tail;
    }
    internal static void Kill(Process process){try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
}
