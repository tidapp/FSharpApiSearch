#!/bin/bash

./bin/FSharpApiSearch.Console/net5.0/FSharpApiSearch.Console.exe "$@" -t:mscorlib -t:FSharp.Core $(for i in lib/*.dll; do echo -n "-t:$(basename $i | sed -e s/.dll$//i) "; done)
