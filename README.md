# AutoMailChecker
## What Is This
It's a software which a local AI check and arrangement your e-mail to appropriate folders.

## Why It Created
I have four e-mail addresses and I have been received them about 130 per day.
So, I sometimes overlook important e-mails.
Therefore, I created this.

## How To Use
### What Need
|Things|Details|
|:---:|:---:|
|OS|Windows(can't move on MacOS and Linux)|
|Software|.NET 10 SDK|
|Local AI|llama.cpp(lamma-server.exe) and a GGUF-Format model|
|e-mails|Only Gmail or iCloud|

### How To Built
You can buit this on CMD
```
dotnet build AutoMailChecker/AutoMailChecker.sln -c Release
```
After built, You can startup on ``AutoMailChecker/src/AutoMailChecker.App/bin/Release/net10.0-windows/AutoMailChecker.exe``.

