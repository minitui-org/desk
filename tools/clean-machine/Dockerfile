# A machine with nothing but the .NET SDK, to follow minitui.org/desk from the start:
#
#   docker build -t desk-clean tools/clean-machine
#   docker run -it --rm desk-clean
#
# Nothing of Desk's is installed, and ~/.dotnet/tools isn't on PATH, as on a new machine. Everything comes from
# nuget.org. --rm throws it all away on exit.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# What a person's own terminal would have: a UTF-8 locale, and nano and less to look at files with.
RUN apt-get update \
    && apt-get install -y --no-install-recommends locales nano less \
    && sed -i 's/^# *en_US.UTF-8/en_US.UTF-8/' /etc/locale.gen \
    && locale-gen \
    && rm -rf /var/lib/apt/lists/*

ENV LANG=en_US.UTF-8 \
    TERM=xterm-256color \
    SHELL=/bin/bash \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# An ordinary user, so the tools go in ~/.dotnet/tools and Desk's files in ~/.local and ~/.config.
RUN useradd --create-home --shell /bin/bash user
USER user
WORKDIR /home/user
CMD ["bash", "-l"]
