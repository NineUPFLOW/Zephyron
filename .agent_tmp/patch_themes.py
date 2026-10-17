p = 'gui/Zephyron.Gui/Theme/Themes.cs'
s = open(p).read()

s = s.replace("""                    Current = wanted;
                    WideHome = theme.HomeFullWidth;""",
"""                    Current = wanted;
                    WideHome = theme.HomeFullWidth;
                    BackdropDeco = theme.Background is null || !look.Background;""")

s = s.replace("""        Current = fallback == LightPath ? "light" : DefaultId;
        WideHome = false;""",
"""        Current = fallback == LightPath ? "light" : DefaultId;
        WideHome = false;
        BackdropDeco = true;""")

s = s.replace("""                Swap(dictionary);
                Glass.Set(dictionary["GlassImage"] as ImageSource, System.Windows.Media.Stretch.UniformToFill);
                BackdropVideo.Take(dictionary);
            }
            else
            {
                BackdropVideo.Discard(dictionary);
            }""",
"""                Swap(dictionary);
                Glass.Set(dictionary["GlassImage"] as ImageSource, System.Windows.Media.Stretch.UniformToFill);
                BackdropVideo.Take(dictionary);
                BackdropDeco = dictionary["BackdropDecoOpacity"] is double shown && shown >= 1;
            }
            else
            {
                BackdropVideo.Discard(dictionary);
            }""")

s = s.replace("""        Swap(builtIn);
        Glass.Set(null, System.Windows.Media.Stretch.UniformToFill);
        BackdropVideo.Take(builtIn);
        Current = DefaultId;
    }""",
"""        Swap(builtIn);
        Glass.Set(null, System.Windows.Media.Stretch.UniformToFill);
        BackdropVideo.Take(builtIn);
        Current = DefaultId;
        BackdropDeco = true;
    }""")

open(p, 'w').write(s)
print("ok")
