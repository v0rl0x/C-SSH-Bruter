# C-SSH-Bruter
C# SSH Bruter, multi functionality including telegram bot outputting and AS details.

ONLY FOR EDUCATIONAL PURPOSES! I do not promote illegal usage of this program. Test only on servers you own.

Setting Up and Running the SSH Login Script on CentOS and Ubuntu:

1. Install .NET Core SDK:
--------------------------------

### CentOS:
- Update package list:

1. sudo yum update -yy

- Register Microsoft's key and repository:

1. sudo rpm --import https://packages.microsoft.com/keys/microsoft.asc

2. sudo sh -c 'echo -e "[packages-microsoft-com-prod]\nname=packages-microsoft-com-prod \nbaseurl= https://packages.microsoft.com/yumrepos/microsoft-rhel7.3-prod\nenabled=1\ngpgcheck=1\ngpgkey=https://packages.microsoft.com/keys/microsoft.asc" > /etc/yum.repos.d/dotnetdev.repo'

3. sudo yum install dotnet-sdk-6.0 -yy

- Install Required Libraries:

1. dotnet add package Renci.SshNet --version 1.0.0
2. dotnet add package Telegram.Bot -- version 18.0.0
3. dotnet add package Newtonsoft.Json -- version 13.0.3

### Ubuntu:
- Update package list:

1. sudo apt update

- Install .NET Core SDK:

1. sudo apt install -y dotnet-sdk-6.0

- Install Required Libraries:

1. dotnet add package Renci.SshNet --version 1.0.0
2. dotnet add package Telegram.Bot -- version 18.0.0
3. dotnet add package Newtonsoft.Json -- version 13.0.3

### Running the Script on both OS:

dotnet run ipfile.txt loginsfile.txt port threads "commandtorunonservers"
