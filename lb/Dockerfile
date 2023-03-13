FROM nginx:1.17.2-alpine

RUN apk --no-cache add curl

RUN mkdir -p /visualizer \
    && curl -SL https://tenka1.klab.jp/2022/visualizer.tar.gz \
    | tar -xzC /visualizer

COPY nginx.conf /etc/nginx/nginx.conf
COPY mime.types /etc/nginx/mime.types
