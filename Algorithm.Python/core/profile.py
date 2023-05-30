import pstats
import subprocess

if __name__ == '__main__':
    fn = r'C:\repos\quantconnect\Lean\Launcher\bin\Debug\profile.stats'
    stats = pstats.Stats(fn)
    # stats.print_stats()
    subprocess.call(['snakeviz', 'C:\\repos\\quantconnect\\Lean\\Launcher\\bin\\Debug\\profile.stats'])
