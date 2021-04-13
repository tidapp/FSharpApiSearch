#!/bin/bash

if uname -a | grep -qi microsoft; then
    ext=.exe
fi

exec ./bin/FSharpApiSearch.Console/net5.0/FSharpApiSearch.Console$ext "$@" -t:mscorlib -t:FSharp.Core $(for i in lib/*.dll; do echo -n "-t:$(basename $i | sed -e s/.dll$//i) "; done)
