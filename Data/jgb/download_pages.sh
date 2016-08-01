#!/usr/bin/env bash

if ! [ -d "search_result_pages" ] ; then
	mkdir search_result_pages
fi

if ! [ -d "show_pages" ] ; then
	mkdir show_pages
fi

USER_AGENT="Mozilla/5.0 (iPhone; CPU iPhone OS 10_0 like Mac OS X) AppleWebKit/602.1.32 (KHTML, like Gecko) Version/10.0 Mobile/14A5261v Safari/602.1"

curl -o search_result_pages/jg_gd_001.html "http://jerrygarcia.com/shows/?srt=DO&kw=&bid%5B9019%5D=on&bid%5B9020%5D=on&bid%5B3533%5D=on&bid%5B9021%5D=on&bid%5B3543%5D=on&bid%5B3551%5D=on&bid%5B3556%5D=on&bid%5B3517%5D=on&bid%5B3525%5D=on&bid%5B3544%5D=on&bid%5B3522%5D=on&bid%5B3535%5D=on&bid%5B3534%5D=on&bid%5B3564%5D=on&bid%5B3519%5D=on&bid%5B3531%5D=on&bid%5B3540%5D=on&bid%5B9022%5D=on&bid%5B9023%5D=on&sd=&ed=&reg=&stat=&ec=&octy=&cty="

for i in {2..78}
do
	FILE=search_result_pages/jg_gd_`printf "%03d" $i`.html

	if ! [ -f "$FILE" ] ; then
		curl -A "$USER_AGENT" -o "$FILE" "http://jerrygarcia.com/shows/page/$i/?srt=DO&kw&bid%5B9019%5D=on&bid%5B9020%5D=on&bid%5B3533%5D=on&bid%5B9021%5D=on&bid%5B3543%5D=on&bid%5B3551%5D=on&bid%5B3556%5D=on&bid%5B3517%5D=on&bid%5B3525%5D=on&bid%5B3544%5D=on&bid%5B3522%5D=on&bid%5B3535%5D=on&bid%5B3534%5D=on&bid%5B3564%5D=on&bid%5B3519%5D=on&bid%5B3531%5D=on&bid%5B3540%5D=on&bid%5B9022%5D=on&bid%5B9023%5D=on&sd&ed&reg&stat&ec&octy&cty"	
	fi
done

grep -hr "class=\"data-display\" href=\"\(.*\)\"" search_result_pages | cut -c 37- | rev | cut -c 3- | rev > show_pages_to_download.txt

while read -r url; do
	FILE="show_pages/${url:28:${#url}-28-1}.html"

	if ! [ -f "$FILE" ] ; then
	    curl -A "$USER_AGENT" -o "$FILE" "$url"
	fi
done < show_pages_to_download.txt

